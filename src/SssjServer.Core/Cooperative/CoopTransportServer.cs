using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace SssjServer.Core.Cooperative;

/// <summary>
/// Original fight-frame TCP/UDP endpoint. Explicitly started by an owning backend; the game app does not start it yet.
/// It handles transport/loading only and never sends a fight-start message or simulates combat.
/// </summary>
public sealed class CoopTransportServer : IAsyncDisposable
{
    private sealed class TcpPeer(TcpClient client)
    {
        public readonly TcpClient Client = client;
        public readonly SemaphoreSlim WriteLock = new(1, 1);
    }
    private readonly CancellationTokenSource _stop = new();
    private readonly TcpListener _tcp;
    private readonly UdpClient _udp;
    private readonly ConcurrentDictionary<string, TcpPeer> _peers = new();
    private readonly ConcurrentDictionary<string, IPEndPoint> _datagrams = new();
    private readonly ConcurrentDictionary<long, Task> _connections = new();
    private readonly Task _accept, _receive;
    private readonly TimeSpan _readIdleTimeout;
    private long _nextId;
    private int _disposed;
    public CoopTransportRooms Rooms { get; }
    public int Port { get; }
    public const int MaxConnections = 64;

    public CoopTransportServer(CoopTransportRooms rooms, IPAddress? address = null, int port = 0, TimeSpan? readIdleTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(rooms);
        _readIdleTimeout = readIdleTimeout ?? TimeSpan.FromSeconds(30);
        if (_readIdleTimeout <= TimeSpan.Zero || _readIdleTimeout > TimeSpan.FromMinutes(30)) throw new ArgumentOutOfRangeException(nameof(readIdleTimeout));
        Rooms = rooms;
        address ??= IPAddress.Loopback;
        _tcp = new TcpListener(address, port);
        _tcp.Start();
        Port = ((IPEndPoint)_tcp.LocalEndpoint).Port;
        try { _udp = new UdpClient(new IPEndPoint(address, Port)); }
        catch { _tcp.Stop(); throw; }
        _accept = AcceptAsync();
        _receive = ReceiveUdpAsync();
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _tcp.AcceptTcpClientAsync(_stop.Token);
                if (_peers.Count >= MaxConnections) { client.Dispose(); continue; }
                client.NoDelay = true;
                long id = Interlocked.Increment(ref _nextId);
                string key = "tcp:" + id;
                var peer = new TcpPeer(client);
                _peers.AddOrUpdate(key, peer, (_, _) => peer);
                var task = HandleTcpAsync(key, peer);
                _connections[id] = task;
                _ = task.ContinueWith(completed => { _connections.TryRemove(id, out var _); }, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (SocketException) when (_stop.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
    }

    private async Task HandleTcpAsync(string key, TcpPeer peer)
    {
        var buffer = new CoopFrameBuffer();
        var bytes = new byte[4096];
        try
        {
            var stream = peer.Client.GetStream();
            while (!_stop.IsCancellationRequested)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                idle.CancelAfter(_readIdleTimeout);
                int count = await stream.ReadAsync(bytes, idle.Token);
                if (count == 0) break;
                foreach (var frame in buffer.Feed(bytes.AsSpan(0, count), CoopFrameDirection.ClientToServer))
                {
                    if (!Rooms.Handle(frame, key, out var deliveries)) return;
                    await SendAsync(deliveries);
                }
            }
        }
        catch (Exception e) when (e is IOException or SocketException or InvalidDataException or OperationCanceledException or ObjectDisposedException)
        { /* Invalid or disconnected transport closes only this peer. Never logs connection codes or raw player data. */ }
        finally
        {
            Rooms.Detach(key);
            _peers.TryRemove(key, out var _);
            peer.Client.Dispose();
        }
    }

    private async Task ReceiveUdpAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                UdpReceiveResult received;
                try { received = await _udp.ReceiveAsync(_stop.Token); }
                catch (SocketException e) when (e.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionRefused && !_stop.IsCancellationRequested)
                { continue; }
                string key = "udp:" + received.RemoteEndPoint;
                try
                {
                    var frame = CoopFrameCodec.DecodeDatagram(received.Buffer, CoopFrameDirection.ClientToServer);
                    if (!Rooms.Handle(frame, key, out var deliveries)) continue;
                    _datagrams[key] = received.RemoteEndPoint;
                    await SendAsync(deliveries);
                }
                catch (InvalidDataException) { }
                finally
                {
                    foreach (var destination in _datagrams.Keys)
                        if (!Rooms.HasBinding(destination)) _datagrams.TryRemove(destination, out var _);
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (SocketException) when (_stop.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
    }

    public Task PermitLoadingAsync(ulong roomId) => SendAsync(Rooms.PermitLoading(roomId));

    public void Revoke(ulong roomId)
    {
        foreach (var destination in Rooms.Revoke(roomId))
        {
            if (_peers.TryGetValue(destination, out var peer)) peer.Client.Dispose();
            _datagrams.TryRemove(destination, out var _);
        }
    }

    private async Task SendAsync(IReadOnlyList<CoopDelivery> deliveries)
    {
        foreach (var delivery in deliveries)
        {
            if (_peers.TryGetValue(delivery.Destination, out var peer))
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    await peer.WriteLock.WaitAsync(timeout.Token);
                    try { await peer.Client.GetStream().WriteAsync(delivery.Frame, timeout.Token); }
                    finally { peer.WriteLock.Release(); }
                }
                catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
                { peer.Client.Dispose(); Rooms.Detach(delivery.Destination); }
            }
            else if (_datagrams.TryGetValue(delivery.Destination, out var endpoint))
            {
                try { await _udp.SendAsync(delivery.Frame, endpoint, _stop.Token); }
                catch (Exception e) when (e is SocketException or OperationCanceledException or ObjectDisposedException)
                { _datagrams.TryRemove(delivery.Destination, out var _); Rooms.Detach(delivery.Destination); }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel(); _tcp.Stop(); _udp.Dispose();
        foreach (var peer in _peers.Values) peer.Client.Dispose();
        await Task.WhenAll(_accept, _receive);
        await Task.WhenAll(_connections.Values.ToArray());
        Rooms.CloseAll(); _datagrams.Clear(); _stop.Dispose();
    }
}

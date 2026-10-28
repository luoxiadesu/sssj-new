using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using SssjServer.Core.Cooperative;
using Xunit;

namespace SssjServer.Tests;

public sealed class CoopTransportTests
{
    private const uint Map = (4u << 16) | 1;
    private static byte[] Hex(string value) => Convert.FromHexString(value.Replace(" ", ""));
    private static CoopFrame Frame(CoopTransportTicket ticket, params CoopCommand[] commands) => new(ticket.Index, ticket.Code, commands);
    private static CoopCommand Cmd(byte type, params byte[] body) => new(type, body);
    private static CoopFrame Ready(CoopTransportTicket ticket) => Frame(ticket, Cmd(1));
    private static CoopFrame Loaded(CoopTransportTicket ticket) => Frame(ticket, Cmd(3));
    private static CoopCommand Input(byte field, uint version, ushort entity = 0, byte group = 1)
    {
        // Original status layout: total u16, group u8, entity u16, count u8,
        // then field total u16, field id u8, version u32, field-specific data.
        var body = Hex("0F0001000001090002010000001234");
        body[2] = group; BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(3), entity);
        body[8] = field; BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(9), version);
        return new(28, body);
    }
    private static IReadOnlyList<CoopTransportTicket> Reserve(CoopTransportRooms rooms, ulong room = 7, params ulong[] roles)
        => rooms.Reserve(room, Map, roles.Length == 0 ? new ulong[] { 101, 202 } : roles, TimeSpan.FromMinutes(5));
    private static CoopTransportTicket[] LoadRoom(CoopTransportRooms rooms)
    {
        var tickets = Reserve(rooms).ToArray();
        for (int i = 0; i < tickets.Length; i++) Assert.True(rooms.Handle(Ready(tickets[i]), "peer" + i, out _));
        Assert.Equal(2, rooms.PermitLoading(7).Count);
        for (int i = 0; i < tickets.Length; i++) Assert.True(rooms.Handle(Loaded(tickets[i]), "peer" + i, out _));
        return tickets;
    }

    // Literal wire examples derived independently from the original ARM64 serializers, NOT device captures.
    [Theory]
    [InlineData("03000278563412030001", 2, 0x12345678u, 1)]
    [InlineData("0700014433221107002804030201", 1, 0x11223344u, 40)]
    public void NativeClientWireExamples(string hex, byte index, uint code, byte type)
    {
        var bytes = Hex(hex);
        var frame = CoopFrameCodec.DecodeDatagram(bytes, CoopFrameDirection.ClientToServer);
        Assert.Equal(index, frame.PlayerIndex); Assert.Equal(code, frame.Code);
        Assert.Equal(type, Assert.Single(frame.Commands).Type);
        Assert.Equal(bytes, CoopFrameCodec.Encode(frame, CoopFrameDirection.ClientToServer));
    }
    [Theory]
    [InlineData("08000800020201000400", 2, 2)]
    [InlineData("040004000104", 1, 4)]
    [InlineData("08000800012904030201", 1, 41)]
    public void NativeServerWireExamples(string hex, byte subject, byte type)
    {
        var bytes = Hex(hex);
        var frame = CoopFrameCodec.DecodeDatagram(bytes, CoopFrameDirection.ServerToClient);
        var command = Assert.Single(frame.Commands);
        Assert.Equal(subject, command.Subject); Assert.Equal(type, command.Type);
        Assert.Equal(bytes, CoopFrameCodec.Encode(frame, CoopFrameDirection.ServerToClient));
    }

    [Fact]
    public void EveryTcpSplitAndCoalescingBoundaryPreservesFrames()
    {
        var first = Hex("03000278563412030001"); var second = Hex("0700014433221107002804030201");
        var bytes = first.Concat(second).ToArray();
        for (int split = 0; split <= bytes.Length; split++)
        {
            var buffer = new CoopFrameBuffer();
            var frames = buffer.Feed(bytes.AsSpan(0, split), CoopFrameDirection.ClientToServer)
                .Concat(buffer.Feed(bytes.AsSpan(split), CoopFrameDirection.ClientToServer)).ToArray();
            Assert.Equal(new byte[] { 1, 40 }, frames.Select(f => f.Commands[0].Type));
            Assert.False(buffer.HasPartialFrame);
        }
        var oneAtATime = new CoopFrameBuffer(); var received = new List<CoopFrame>();
        foreach (byte value in bytes) received.AddRange(oneAtATime.Feed(new[] { value }, CoopFrameDirection.ClientToServer));
        Assert.Equal(2, received.Count);
    }
    [Theory]
    [InlineData("00000000000000")] // empty outer payload
    [InlineData("020000000000000200")] // command cannot fit its type
    [InlineData("03000000000000020001")] // inner size excludes its prefix (wrong)
    [InlineData("03000000000000040001")] // inner size exceeds frame
    [InlineData("0400000000000003000100")] // unconsumed byte
    public void MalformedLengthsPoisonStream(string hex)
    {
        var buffer = new CoopFrameBuffer();
        Assert.Throws<InvalidDataException>(() => buffer.Feed(Hex(hex), CoopFrameDirection.ClientToServer));
        Assert.Throws<InvalidDataException>(() => buffer.Feed(Hex("03000278563412030001"), CoopFrameDirection.ClientToServer));
    }
    [Theory]
    [InlineData("030002785634120300")] // truncated frame
    [InlineData("0300027856341203000100")] // trailing bytes
    [InlineData("0300027856341203000103000278563412030001")] // two frames in a UDP datagram
    public void UdpRequiresExactlyOneFrame(string hex)
        => Assert.Throws<InvalidDataException>(() => CoopFrameCodec.DecodeDatagram(Hex(hex), CoopFrameDirection.ClientToServer));
    [Fact]
    public void CodecBoundsAndCopiesNetworkBuffers()
    {
        var bytes = Hex("0700014433221107002804030201");
        var frame = CoopFrameCodec.DecodeDatagram(bytes, CoopFrameDirection.ClientToServer);
        bytes[^1] = 255; Assert.Equal(1, frame.Commands[0].Body.Span[^1]);
        Assert.Throws<ArgumentException>(() => CoopFrameCodec.Encode(new(0, 1, Array.Empty<CoopCommand>()), CoopFrameDirection.ClientToServer));
        Assert.Throws<ArgumentException>(() => CoopFrameCodec.Encode(new(0, 1, Enumerable.Repeat(Cmd(1), 257).ToArray()), CoopFrameDirection.ClientToServer));
        Assert.Throws<ArgumentException>(() => CoopFrameCodec.Encode(new(0, 1, new[] { new CoopCommand(1, new byte[65533]) }), CoopFrameDirection.ClientToServer));
        var buffer = new CoopFrameBuffer();
        Assert.Throws<InvalidDataException>(() => buffer.Feed(new byte[150000], CoopFrameDirection.ClientToServer));
    }
    [Fact]
    public void MaximumPayloadAndCommandCountsRemainUnambiguous()
    {
        var frame = new CoopFrame(255, uint.MaxValue, new[] { new CoopCommand(28, new byte[65532]) });
        var bytes = CoopFrameCodec.Encode(frame, CoopFrameDirection.ClientToServer);
        Assert.Equal(65542, bytes.Length);
        Assert.Equal(65532, CoopFrameCodec.DecodeDatagram(bytes, CoopFrameDirection.ClientToServer).Commands[0].Body.Length);
        var many = new CoopFrame(0, 1, Enumerable.Repeat(Cmd(10), 256).ToArray());
        Assert.Equal(256, CoopFrameCodec.DecodeDatagram(CoopFrameCodec.Encode(many, CoopFrameDirection.ClientToServer), CoopFrameDirection.ClientToServer).Commands.Count);
    }

    [Fact]
    public void ArbitraryTruncatedOrMutatedFramesNeverEscapeWithIndexErrors()
    {
        var random = new Random(17492);
        for (int i = 0; i < 10000; i++)
        {
            var bytes = new byte[random.Next(0, 128)]; random.NextBytes(bytes);
            if (bytes.Length > 2 && i % 2 == 0) BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)random.Next(bytes.Length));
            foreach (var direction in new[] { CoopFrameDirection.ClientToServer, CoopFrameDirection.ServerToClient })
                try
                {
                    if (CoopFrameCodec.TryDecode(bytes, direction, out var frame, out int consumed))
                        Assert.Equal(bytes.AsSpan(0, consumed).ToArray(), CoopFrameCodec.Encode(frame, direction));
                }
                catch (InvalidDataException) { }
        }
    }

    [Fact]
    public void NativeStatusLayoutParsesNestedInclusiveLengths()
    {
        var status = CoopStatusPacket.Decode(Hex("1500010200010F0002010000000000803F00000000"));
        Assert.Equal(1, status.Group); Assert.Equal(2, status.Entity);
        var field = Assert.Single(status.Fields); Assert.Equal(2, field.Id); Assert.Equal(1u, field.Version);
        Assert.Equal(Hex("0000803F00000000"), field.Data.ToArray());
    }
    [Theory]
    [InlineData("050001000000")]
    [InlineData("060005000000")]
    [InlineData("060001000001")]
    [InlineData("0D000100000106000201000000")]
    [InlineData("0E00010000010D00020100000000")]
    [InlineData("0E00010000010700020100000000")]
    [InlineData("1400010000020700020100000007000202000000")]
    public void MalformedOrDuplicateStatusFieldsAreRejected(string hex)
        => Assert.Throws<InvalidDataException>(() => CoopStatusPacket.Decode(Hex(hex)));

    [Fact]
    public void ReservationRejectsDuplicateRolesRoomsAndInvalidLifetimes()
    {
        var rooms = new CoopTransportRooms(); var tickets = Reserve(rooms);
        Assert.Equal(2, tickets.Select(t => t.Code).Distinct().Count()); Assert.All(tickets, t => Assert.NotEqual(0u, t.Code));
        Assert.Throws<InvalidOperationException>(() => Reserve(rooms));
        Assert.Throws<InvalidOperationException>(() => Reserve(rooms, 8, 101));
        Assert.Throws<ArgumentException>(() => Reserve(rooms, 9, 303, 303));
        Assert.Throws<ArgumentException>(() => rooms.Reserve(9, Map, new ulong[] { 303 }, TimeSpan.FromHours(1)));
        Assert.Throws<ArgumentException>(() => rooms.Reserve(9, Map, new ulong[] { 303, 404, 505, 606 }, TimeSpan.FromMinutes(1)));
        rooms.Revoke(7); Assert.Single(Reserve(rooms, 8, 101));
    }
    [Fact]
    public void AllClientsMustBeReadyAndBackendMustExplicitlyReleaseLoading()
    {
        var rooms = new CoopTransportRooms(); var t = Reserve(rooms);
        Assert.Throws<InvalidOperationException>(() => rooms.PermitLoading(7));
        Assert.True(rooms.Handle(Ready(t[0]), "a", out var replies)); Assert.Empty(replies);
        Assert.Equal(CoopLoadingPhase.AwaitingClients, rooms.Snapshot(7)!.Phase);
        Assert.False(rooms.Handle(Loaded(t[0]), "a", out _));
        Assert.True(rooms.Handle(Ready(t[1]), "b", out replies)); Assert.Empty(replies);
        Assert.Equal(CoopLoadingPhase.ClientsReady, rooms.Snapshot(7)!.Phase);
        var loading = rooms.PermitLoading(7); Assert.Equal(2, loading.Count);
        foreach (var message in loading)
        {
            var command = Assert.Single(CoopFrameCodec.DecodeDatagram(message.Frame, CoopFrameDirection.ServerToClient).Commands);
            Assert.Equal(2, command.Type); Assert.Equal(Map, BinaryPrimitives.ReadUInt32LittleEndian(command.Body.Span));
        }
        Assert.Empty(rooms.PermitLoading(7));
        Assert.True(rooms.Handle(Loaded(t[0]), "a", out replies)); Assert.Single(replies);
        Assert.Equal(CoopLoadingPhase.Loading, rooms.Snapshot(7)!.Phase);
        Assert.True(rooms.Handle(Loaded(t[1]), "b", out replies)); Assert.Single(replies);
        Assert.Equal(CoopLoadingPhase.Loaded, rooms.Snapshot(7)!.Phase);
        Assert.True(rooms.Handle(Loaded(t[1]), "b", out replies)); Assert.Single(replies);
        Assert.True(rooms.Handle(Ready(t[1]), "b", out replies)); Assert.Empty(replies);
        Assert.Empty(rooms.TakeInputs(7)); // Reaching Loaded never invents combat or a victory.
    }
    [Fact]
    public void RepeatedReadyResendsLostLoadingMapUntilClientAcknowledges()
    {
        var rooms = new CoopTransportRooms(); var t = Reserve(rooms);
        Assert.True(rooms.Handle(Ready(t[0]), "a", out _)); Assert.True(rooms.Handle(Ready(t[1]), "b", out _));
        rooms.PermitLoading(7);
        Assert.True(rooms.Handle(Ready(t[0]), "a", out var replies)); Assert.Single(replies);
    }
    [Fact]
    public void CodeIndexConnectionAndRoomCannotBeSwapped()
    {
        var rooms = new CoopTransportRooms(); var t = Reserve(rooms); var other = Reserve(rooms, 8, 303);
        Assert.False(rooms.Handle(new(t[0].Index, 0, new[] { Cmd(1) }), "a", out _));
        Assert.False(rooms.Handle(Ready(t[0]) with { PlayerIndex = t[1].Index }, "a", out _));
        Assert.True(rooms.Handle(Ready(t[0]), "a", out _));
        Assert.False(rooms.Handle(Ready(t[0]), "stolen", out _));
        Assert.False(rooms.Handle(Ready(t[1]), "a", out _));
        Assert.False(rooms.Handle(Ready(other[0]), "a", out _));
        Assert.False(rooms.Snapshot(8)!.Peers[0].Connected);
    }
    [Theory]
    [InlineData(1, "00")] [InlineData(3, "00")] [InlineData(10, "00")] [InlineData(40, "000000")]
    [InlineData(14, "01")] [InlineData(14, "FF")] [InlineData(14, "0200000000010000000000000002000000")]
    [InlineData(99, "")]
    public void WholeFrameValidationNeverPartiallyBindsOrReadiesPeer(byte type, string hex)
    {
        var rooms = new CoopTransportRooms(); var t = Reserve(rooms);
        Assert.False(rooms.Handle(Frame(t[0], Cmd(1), new(type, Hex(hex))), "a", out var replies));
        Assert.Empty(replies); Assert.False(rooms.Snapshot(7)!.Peers[0].Ready); Assert.False(rooms.Snapshot(7)!.Peers[0].Connected);
    }
    [Fact]
    public void BreathAndSignedPingEchoUseOriginalMessageIds()
    {
        var rooms = new CoopTransportRooms(); var t = Reserve(rooms);
        Assert.True(rooms.Handle(Frame(t[0], Cmd(10), Cmd(40, 255, 255, 255, 255)), "a", out var replies));
        var commands = replies.SelectMany(r => CoopFrameCodec.DecodeDatagram(r.Frame, CoopFrameDirection.ServerToClient).Commands).ToArray();
        Assert.Equal(new byte[] { 10, 41 }, commands.Select(c => c.Type));
        Assert.Equal(-1, BinaryPrimitives.ReadInt32LittleEndian(commands[1].Body.Span));
        Assert.All(commands, c => Assert.Equal(t[0].Index, c.Subject));
    }
    [Fact]
    public void OnlyOwnLocalInputEntersQueueAfterLoadingBarrierAndVersionsAreIdempotent()
    {
        var rooms = new CoopTransportRooms(); var t = LoadRoom(rooms);
        Assert.True(rooms.Handle(Frame(t[0], Input(2, 1)), "peer0", out _));
        Assert.True(rooms.Handle(Frame(t[0], Input(2, 1)), "peer0", out _));
        Assert.True(rooms.Handle(Frame(t[0], Input(2, 0)), "peer0", out _));
        Assert.Equal(1, rooms.Snapshot(7)!.PendingInputs);
        var input = Assert.Single(rooms.TakeInputs(7));
        Assert.Equal(t[0].RoleId, input.RoleId); Assert.Equal(7ul, input.RoomId); Assert.Equal(t[0].Index, input.Index);
        Assert.True(rooms.Handle(Frame(t[0], Input(2, 3)), "peer0", out _));
        Assert.True(rooms.Handle(Frame(t[0], Input(2, 2)), "peer0", out _));
        Assert.Equal(3u, Assert.Single(Assert.Single(rooms.TakeInputs(7)).Status.Fields).Version);
        Assert.False(rooms.Handle(Frame(t[0], Input(2, 4, 1)), "peer0", out _)); // Other player's input
        foreach (byte group in new byte[] { 0, 2, 3, 4 }) Assert.False(rooms.Handle(Frame(t[0], Input(2, 4, 0, group)), "peer0", out _));
        Assert.Empty(rooms.TakeInputs(7));
    }
    [Fact]
    public void PredictedInputBeforeBarrierIsDiscardedWithoutPoisoningFutureVersions()
    {
        var rooms = new CoopTransportRooms(); var t = Reserve(rooms);
        Assert.True(rooms.Handle(Frame(t[0], Cmd(1), Input(2, 50)), "a", out _));
        Assert.True(rooms.Handle(Ready(t[1]), "b", out _)); rooms.PermitLoading(7);
        Assert.True(rooms.Handle(Loaded(t[1]), "b", out _));
        Assert.True(rooms.Handle(Frame(t[0], Cmd(3), Input(2, 1)), "a", out var replies)); Assert.Single(replies);
        Assert.Equal(1u, Assert.Single(Assert.Single(rooms.TakeInputs(7)).Status.Fields).Version);
    }
    [Fact]
    public void InputBackpressureDoesNotAdvanceRejectedVersions()
    {
        var rooms = new CoopTransportRooms(); var t = LoadRoom(rooms);
        for (uint v = 1; v <= CoopTransportRooms.MaxPendingInputs; v++) Assert.True(rooms.Handle(Frame(t[0], Input(2, v)), "peer0", out _));
        Assert.False(rooms.Handle(Frame(t[0], Input(2, 129)), "peer0", out _));
        Assert.Equal(128, rooms.TakeInputs(7).Count);
        Assert.True(rooms.Handle(Frame(t[0], Input(2, 129)), "peer0", out _)); Assert.Single(rooms.TakeInputs(7));
    }
    [Fact]
    public void DisconnectRevocationAndExpirationInvalidateOldTickets()
    {
        var now = DateTimeOffset.UtcNow; var rooms = new CoopTransportRooms(() => now); var t = LoadRoom(rooms);
        rooms.Detach("peer0"); Assert.Equal(CoopLoadingPhase.Interrupted, rooms.Snapshot(7)!.Phase);
        Assert.False(rooms.Handle(Ready(t[0]), "new", out _)); Assert.False(rooms.Handle(Ready(t[1]), "peer1", out _));
        rooms.Revoke(7); var fresh = Reserve(rooms); Assert.NotEqual(t[0].Code, fresh[0].Code);
        Assert.False(rooms.Handle(Ready(t[0]), "a", out _));
        now = now.AddMinutes(5); Assert.Null(rooms.Snapshot(7)); Assert.False(rooms.Handle(Ready(fresh[0]), "a", out _));
    }

    private sealed class TcpTestPeer : IAsyncDisposable
    {
        public readonly TcpClient Client = new() { NoDelay = true };
        public readonly CoopFrameBuffer Buffer = new();
        public readonly Queue<CoopFrame> Frames = new();
        public async Task Send(CoopFrame frame) => await Client.GetStream().WriteAsync(CoopFrameCodec.Encode(frame, CoopFrameDirection.ClientToServer));
        public async Task<CoopFrame> Read()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (Frames.Count == 0)
            {
                var bytes = new byte[4096]; int n = await Client.GetStream().ReadAsync(bytes, timeout.Token);
                if (n == 0) throw new IOException("Cooperative test peer closed");
                foreach (var frame in Buffer.Feed(bytes.AsSpan(0, n), CoopFrameDirection.ServerToClient)) Frames.Enqueue(frame);
            }
            return Frames.Dequeue();
        }
        public ValueTask DisposeAsync() { Client.Dispose(); return ValueTask.CompletedTask; }
    }
    private static async Task Eventually(Func<bool> predicate)
    {
        for (int i = 0; i < 150; i++) { if (predicate()) return; await Task.Delay(20); }
        Assert.True(predicate(), "Cooperative transport did not reach the expected state");
    }
    [Fact]
    public async Task RealTcpPeersLoadWithFragmentedAndCoalescedFrames()
    {
        var rooms = new CoopTransportRooms(); var t = Reserve(rooms);
        await using var host = new CoopTransportServer(rooms);
        await using var a = new TcpTestPeer(); await using var b = new TcpTestPeer();
        await a.Client.ConnectAsync(IPAddress.Loopback, host.Port); await b.Client.ConnectAsync(IPAddress.Loopback, host.Port);
        var ready = CoopFrameCodec.Encode(Ready(t[0]), CoopFrameDirection.ClientToServer);
        foreach (byte value in ready) await a.Client.GetStream().WriteAsync(new byte[] { value });
        await b.Send(Ready(t[1])); await Eventually(() => rooms.Snapshot(7)!.Phase == CoopLoadingPhase.ClientsReady);
        await host.PermitLoadingAsync(7);
        Assert.Equal(2, Assert.Single((await a.Read()).Commands).Type); Assert.Equal(2, Assert.Single((await b.Read()).Commands).Type);
        await a.Send(Loaded(t[0])); Assert.Equal(4, Assert.Single((await a.Read()).Commands).Type);
        var end = CoopFrameCodec.Encode(Loaded(t[1]), CoopFrameDirection.ClientToServer);
        var ping = CoopFrameCodec.Encode(Frame(t[1], Cmd(40, 1, 2, 3, 4)), CoopFrameDirection.ClientToServer);
        await b.Client.GetStream().WriteAsync(end.Concat(ping).ToArray());
        Assert.Equal(4, Assert.Single((await b.Read()).Commands).Type); Assert.Equal(41, Assert.Single((await b.Read()).Commands).Type);
        await Eventually(() => rooms.Snapshot(7)!.Phase == CoopLoadingPhase.Loaded);
        await a.Send(Frame(t[0], Input(2, 1))); await Eventually(() => rooms.Snapshot(7)!.PendingInputs == 1);
        Assert.Equal(t[0].RoleId, Assert.Single(rooms.TakeInputs(7)).RoleId);
    }
    [Fact]
    public async Task RealUdpPeersRespectDatagramBoundariesAndLoadIndependently()
    {
        var rooms = new CoopTransportRooms(); var t = Reserve(rooms);
        await using var host = new CoopTransportServer(rooms);
        using var a = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); using var b = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        a.Connect(IPAddress.Loopback, host.Port); b.Connect(IPAddress.Loopback, host.Port);
        await a.SendAsync(CoopFrameCodec.Encode(Ready(t[0]), CoopFrameDirection.ClientToServer));
        await b.SendAsync(CoopFrameCodec.Encode(Ready(t[1]), CoopFrameDirection.ClientToServer));
        await Eventually(() => rooms.Snapshot(7)!.Phase == CoopLoadingPhase.ClientsReady);
        await host.PermitLoadingAsync(7);
        async Task<byte> Type(UdpClient client)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var received = await client.ReceiveAsync(timeout.Token);
            return Assert.Single(CoopFrameCodec.DecodeDatagram(received.Buffer, CoopFrameDirection.ServerToClient).Commands).Type;
        }
        Assert.Equal(2, await Type(a)); Assert.Equal(2, await Type(b));
        await a.SendAsync(CoopFrameCodec.Encode(Loaded(t[0]), CoopFrameDirection.ClientToServer)); Assert.Equal(4, await Type(a));
        await b.SendAsync(CoopFrameCodec.Encode(Loaded(t[1]), CoopFrameDirection.ClientToServer)); Assert.Equal(4, await Type(b));
        Assert.Equal(CoopLoadingPhase.Loaded, rooms.Snapshot(7)!.Phase);
        await a.SendAsync(new byte[] { 0, 0 }); // Malformed datagram does not stop the UDP receive loop.
        await b.SendAsync(CoopFrameCodec.Encode(Frame(t[1], Cmd(10)), CoopFrameDirection.ClientToServer)); Assert.Equal(10, await Type(b));
    }
    [Fact]
    public async Task MalformedOrWrongCodeTcpCannotPoisonAnotherRoom()
    {
        var rooms = new CoopTransportRooms(); var t = Reserve(rooms); Reserve(rooms, 8, 303);
        await using var host = new CoopTransportServer(rooms);
        await using var bad = new TcpTestPeer(); await using var good = new TcpTestPeer();
        await bad.Client.ConnectAsync(IPAddress.Loopback, host.Port); await good.Client.ConnectAsync(IPAddress.Loopback, host.Port);
        await bad.Send(Ready(t[0]) with { Code = 0 });
        await good.Send(Frame(t[0], Cmd(1), Cmd(10))); Assert.Equal(10, Assert.Single((await good.Read()).Commands).Type);
        Assert.False(rooms.Snapshot(8)!.Peers[0].Connected); Assert.True(rooms.Snapshot(7)!.Peers[0].Ready);
    }
    [Fact]
    public async Task ServerDisposalClosesBothListenersAndRevokesReservations()
    {
        var rooms = new CoopTransportRooms(); var t = Reserve(rooms);
        var host = new CoopTransportServer(rooms); int port = host.Port;
        await using var a = new TcpTestPeer(); await a.Client.ConnectAsync(IPAddress.Loopback, port);
        await a.Send(Frame(t[0], Cmd(1), Cmd(10))); await a.Read();
        await host.DisposeAsync(); await host.DisposeAsync(); Assert.Null(rooms.Snapshot(7));
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
        var listener = new TcpListener(IPAddress.Loopback, port); listener.Start(); listener.Stop();
    }
    [Fact]
    public async Task IdleTcpClosesOnlyItsOwnReservation()
    {
        var rooms = new CoopTransportRooms(); var t = Reserve(rooms); var other = Reserve(rooms, 8, 303);
        await using var host = new CoopTransportServer(rooms, readIdleTimeout: TimeSpan.FromMilliseconds(200));
        await using var peer = new TcpTestPeer(); await peer.Client.ConnectAsync(IPAddress.Loopback, host.Port);
        await peer.Send(Frame(t[0], Cmd(1), Cmd(10))); await peer.Read();
        await Eventually(() => rooms.Snapshot(7)!.Phase == CoopLoadingPhase.Interrupted);
        Assert.NotNull(rooms.Snapshot(8)); Assert.False(rooms.Snapshot(8)!.Peers[0].Connected);
    }
}

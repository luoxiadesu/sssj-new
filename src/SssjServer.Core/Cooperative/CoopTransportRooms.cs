using System.Buffers.Binary;
using System.Security.Cryptography;

namespace SssjServer.Core.Cooperative;

public enum CoopLoadingPhase { AwaitingClients, ClientsReady, Loading, Loaded, Interrupted }
public sealed record CoopTransportTicket(ulong RoomId, ulong RoleId, byte Index, uint Code);
public sealed record CoopTransportPeer(ulong RoleId, byte Index, bool Connected, bool Ready, bool Loaded);
public sealed record CoopTransportSnapshot(ulong RoomId, uint Map, CoopLoadingPhase Phase, IReadOnlyList<CoopTransportPeer> Peers, int PendingInputs);
public sealed record CoopDelivery(string Destination, byte[] Frame);
public sealed record CoopAuthenticatedInput(ulong RoomId, ulong RoleId, byte Index, CoopStatusPacket Status);

/// <summary>
/// Ephemeral transport reservations supplied by a trusted lobby/backend. No player files, rewards, battle results or simulation.
/// Loading is released explicitly only after the future simulation backend has prepared the scene.
/// </summary>
public sealed class CoopTransportRooms
{
    private sealed class Peer(CoopTransportTicket ticket)
    {
        public readonly CoopTransportTicket Ticket = ticket;
        public string? Binding;
        public bool Ready, Loaded, Interrupted;
        public readonly Dictionary<byte, uint> InputVersions = new();
    }
    private sealed class Room(ulong id, uint map, DateTimeOffset expires, Peer[] peers)
    {
        public readonly ulong Id = id;
        public readonly uint Map = map;
        public readonly DateTimeOffset Expires = expires;
        public readonly Peer[] Peers = peers;
        public bool LoadingPermitted;
        public readonly Queue<CoopAuthenticatedInput> Inputs = new();
    }
    private readonly object _sync = new();
    private readonly Dictionary<ulong, Room> _rooms = new();
    private readonly Dictionary<ulong, Room> _roles = new();
    private readonly Dictionary<uint, (Room Room, Peer Peer)> _codes = new();
    private readonly Dictionary<string, Peer> _bindings = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> _clock;
    public const int MaxPendingInputs = 128;

    public CoopTransportRooms(Func<DateTimeOffset>? clock = null) => _clock = clock ?? (() => DateTimeOffset.UtcNow);

    public IReadOnlyList<CoopTransportTicket> Reserve(ulong roomId, uint map, IReadOnlyList<ulong> roles, TimeSpan lifetime)
    {
        if (roomId == 0 || map == 0 || roles.Count is < 1 or > 3 || roles.Any(r => r == 0) || roles.Distinct().Count() != roles.Count
            || lifetime <= TimeSpan.Zero || lifetime > TimeSpan.FromMinutes(30)) throw new ArgumentException("Invalid cooperative reservation");
        lock (_sync)
        {
            Sweep();
            if (_rooms.ContainsKey(roomId)) throw new InvalidOperationException("Cooperative room is already reserved");
            if (roles.Any(_roles.ContainsKey)) throw new InvalidOperationException("Cooperative role already has a reservation");
            var peers = new Peer[roles.Count];
            var used = new HashSet<uint>(_codes.Keys);
            for (int i = 0; i < roles.Count; i++)
            {
                uint code;
                do { code = BinaryPrimitives.ReadUInt32LittleEndian(RandomNumberGenerator.GetBytes(4)); } while (code == 0 || !used.Add(code));
                peers[i] = new Peer(new(roomId, roles[i], (byte)i, code));
            }
            var room = new Room(roomId, map, _clock().Add(lifetime), peers);
            _rooms.Add(roomId, room);
            foreach (var peer in peers) { _codes.Add(peer.Ticket.Code, (room, peer)); _roles.Add(peer.Ticket.RoleId, room); }
            return Array.AsReadOnly(peers.Select(p => p.Ticket).ToArray());
        }
    }

    private static CoopLoadingPhase Phase(Room room) => room.Peers.Any(p => p.Interrupted) ? CoopLoadingPhase.Interrupted
        : room.LoadingPermitted ? room.Peers.All(p => p.Loaded) ? CoopLoadingPhase.Loaded : CoopLoadingPhase.Loading
        : room.Peers.All(p => p.Ready) ? CoopLoadingPhase.ClientsReady : CoopLoadingPhase.AwaitingClients;

    public CoopTransportSnapshot? Snapshot(ulong roomId)
    {
        lock (_sync)
        {
            Sweep();
            return !_rooms.TryGetValue(roomId, out var room) ? null : new(room.Id, room.Map, Phase(room),
                Array.AsReadOnly(room.Peers.Select(p => new CoopTransportPeer(p.Ticket.RoleId, p.Ticket.Index, p.Binding is not null, p.Ready, p.Loaded)).ToArray()), room.Inputs.Count);
        }
    }

    public IReadOnlyList<CoopDelivery> PermitLoading(ulong roomId)
    {
        lock (_sync)
        {
            Sweep();
            if (!_rooms.TryGetValue(roomId, out var room) || Phase(room) is not (CoopLoadingPhase.ClientsReady or CoopLoadingPhase.Loading or CoopLoadingPhase.Loaded))
                throw new InvalidOperationException("Cooperative clients are not ready");
            if (room.LoadingPermitted) return Array.Empty<CoopDelivery>();
            room.LoadingPermitted = true;
            return room.Peers.Select(p => Delivery(p, 2, UInt(room.Map))).ToArray();
        }
    }

    /// <summary>Validates the WHOLE decoded frame before binding or changing any peer state.</summary>
    public bool Handle(CoopFrame frame, string destination, out IReadOnlyList<CoopDelivery> deliveries)
    {
        ArgumentException.ThrowIfNullOrEmpty(destination);
        deliveries = Array.Empty<CoopDelivery>();
        if (frame.Commands.Count is < 1 or > CoopFrameCodec.MaxCommands) return false;
        lock (_sync)
        {
            Sweep();
            if (!_codes.TryGetValue(frame.Code, out var slot) || slot.Peer.Ticket.Index != frame.PlayerIndex) return false;
            var (room, peer) = slot;
            if (Phase(room) == CoopLoadingPhase.Interrupted || peer.Interrupted || peer.Binding is not null && peer.Binding != destination
                || _bindings.TryGetValue(destination, out var bound) && bound != peer) return false;
            bool willReady = peer.Ready, willLoad = peer.Loaded;
            var statuses = new List<CoopStatusPacket>();
            try
            {
                foreach (var command in frame.Commands)
                {
                    var body = command.Body.Span;
                    switch (command.Type)
                    {
                        case 1 when body.IsEmpty: willReady = true; break;
                        case 3 when body.IsEmpty:
                            if (!willReady || !room.LoadingPermitted) return false;
                            willLoad = true; break;
                        case 10 when body.IsEmpty: break;
                        case 40 when body.Length == 4: break;
                        case 14:
                            if (body.IsEmpty || body[0] > 254 || body.Length != 1 + 8 * body[0]) return false;
                            var versionIds = new HashSet<int>();
                            for (int i = 0; i < body[0]; i++)
                                if (!versionIds.Add(BinaryPrimitives.ReadInt32LittleEndian(body[(1+8*i)..]))) return false;
                            // Shape is verified. No state snapshots exist yet to acknowledge.
                            break;
                        case 28:
                            var status = CoopStatusPacket.Decode(body);
                            // Native dispatch group 1 is LocalStatus. Never accept client monster/HP/room/global state.
                            if (status.Group != 1 || status.Entity != peer.Ticket.Index) return false;
                            statuses.Add(status); break;
                        default: return false;
                    }
                }
            }
            catch (InvalidDataException) { return false; }
            bool allLoaded = room.LoadingPermitted && willLoad && room.Peers.Where(p => p != peer).All(p => p.Loaded);
            // Before the loading barrier, native clients may already send predicted input; discard it without applying versions.
            var pending = new List<CoopStatusPacket>();
            var versions = new Dictionary<byte, uint>(peer.InputVersions);
            if (allLoaded)
                foreach (var status in statuses)
                {
                    var changed = status.Fields.Where(f => f.Version > versions.GetValueOrDefault(f.Id)).ToArray();
                    foreach (var field in changed) versions[field.Id] = field.Version;
                    if (changed.Length > 0) pending.Add(status with { Fields = Array.AsReadOnly(changed) });
                }
            if (pending.Count > MaxPendingInputs - room.Inputs.Count) return false;
            if (peer.Binding is null) { peer.Binding = destination; _bindings.Add(destination, peer); }
            peer.Ready = willReady; peer.Loaded = willLoad;
            peer.InputVersions.Clear(); foreach (var entry in versions) peer.InputVersions.Add(entry.Key, entry.Value);
            foreach (var status in pending) room.Inputs.Enqueue(new(room.Id, peer.Ticket.RoleId, peer.Ticket.Index, status));
            var responses = new List<CoopDelivery>();
            foreach (var command in frame.Commands)
                switch (command.Type)
                {
                    case 1 when room.LoadingPermitted && !peer.Loaded: responses.Add(Delivery(peer, 2, UInt(room.Map))); break;
                    case 3: responses.Add(Delivery(peer, 4, Array.Empty<byte>())); break;
                    case 10: responses.Add(Delivery(peer, 10, Array.Empty<byte>())); break;
                    case 40: responses.Add(Delivery(peer, 41, command.Body.ToArray())); break;
                }
            deliveries = responses;
            return true;
        }
    }

    public IReadOnlyList<CoopAuthenticatedInput> TakeInputs(ulong roomId)
    {
        lock (_sync)
        {
            Sweep();
            if (!_rooms.TryGetValue(roomId, out var room)) return Array.Empty<CoopAuthenticatedInput>();
            var inputs = room.Inputs.ToArray(); room.Inputs.Clear(); return inputs;
        }
    }

    public bool HasBinding(string destination)
    { lock (_sync) { Sweep(); return _bindings.ContainsKey(destination); } }

    // Native reconnect notification/ticket renewal is a later layer. Disconnected reservations are interrupted and cannot be replayed.
    public void Detach(string destination)
    {
        lock (_sync)
        {
            if (!_bindings.Remove(destination, out var peer)) return;
            peer.Binding = null; peer.Interrupted = true; peer.Ready = false; peer.Loaded = false;
            _codes.Remove(peer.Ticket.Code);
            if (_rooms.TryGetValue(peer.Ticket.RoomId, out var room)) room.Inputs.Clear();
        }
    }

    public IReadOnlyList<string> Revoke(ulong roomId)
    {
        lock (_sync)
        {
            if (!_rooms.Remove(roomId, out var room)) return Array.Empty<string>();
            var bindings = room.Peers.Where(p => p.Binding is not null).Select(p => p.Binding!).ToArray();
            foreach (var peer in room.Peers)
            { _codes.Remove(peer.Ticket.Code); _roles.Remove(peer.Ticket.RoleId); if (peer.Binding is not null) _bindings.Remove(peer.Binding); }
            room.Inputs.Clear();
            return bindings;
        }
    }

    public void CloseAll() { lock (_sync) foreach (ulong id in _rooms.Keys.ToArray()) Revoke(id); }
    private void Sweep() { foreach (var room in _rooms.Values.Where(r => r.Expires <= _clock()).ToArray()) Revoke(room.Id); }
    private static byte[] UInt(uint value) { var body = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(body, value); return body; }
    private static CoopDelivery Delivery(Peer peer, byte type, byte[] body) => new(peer.Binding!,
        CoopFrameCodec.Encode(new(0, 0, new[] { new CoopCommand(type, body, peer.Ticket.Index) }), CoopFrameDirection.ServerToClient));
}

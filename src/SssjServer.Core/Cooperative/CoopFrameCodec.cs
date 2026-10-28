using System.Buffers.Binary;

namespace SssjServer.Core.Cooperative;

public enum CoopFrameDirection { ClientToServer, ServerToClient }

public sealed record CoopCommand(byte Type, ReadOnlyMemory<byte> Body, byte Subject = 0);
public sealed record CoopFrame(byte PlayerIndex, uint Code, IReadOnlyList<CoopCommand> Commands);

/// <summary>Original Online.Frame wire format; separate from the 16-byte gate/protobuf protocol.</summary>
public static class CoopFrameCodec
{
    public const int MaxPayloadLength = ushort.MaxValue;
    public const int ClientHeaderLength = 7;
    public const int ServerHeaderLength = 2;
    // Local parsing bound, not an original-server configuration value.
    public const int MaxCommands = 256;

    public static bool TryDecode(ReadOnlySpan<byte> bytes, CoopFrameDirection direction, out CoopFrame frame, out int consumed)
    {
        frame = null!; consumed = 0;
        if (bytes.Length < 2) return false;
        bool client = direction == CoopFrameDirection.ClientToServer;
        int header = client ? ClientHeaderLength : ServerHeaderLength;
        int minimumCommand = client ? 3 : 4;
        int length = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        if (length < minimumCommand) throw new InvalidDataException("Empty or undersized cooperative frame");
        int total = header + length;
        if (bytes.Length < total) return false;
        var commands = new List<CoopCommand>();
        for (int offset = header; offset < total;)
        {
            if (total - offset < minimumCommand) throw new InvalidDataException("Truncated cooperative command header");
            int size = BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]);
            // Inner command size INCLUDES its two-byte size field; outer length excludes the entire frame header.
            if (size < minimumCommand || size > total - offset) throw new InvalidDataException("Invalid cooperative command size");
            byte subject = client ? (byte)0 : bytes[offset + 2];
            byte type = bytes[offset + minimumCommand - 1];
            commands.Add(new CoopCommand(type, bytes.Slice(offset + minimumCommand, size - minimumCommand).ToArray(), subject));
            if (commands.Count > MaxCommands) throw new InvalidDataException("Too many cooperative commands");
            offset += size;
        }
        frame = new CoopFrame(client ? bytes[2] : (byte)0, client ? BinaryPrimitives.ReadUInt32LittleEndian(bytes[3..]) : 0, commands.AsReadOnly());
        consumed = total;
        return true;
    }

    public static CoopFrame DecodeDatagram(ReadOnlySpan<byte> bytes, CoopFrameDirection direction)
    {
        if (!TryDecode(bytes, direction, out var frame, out int consumed) || consumed != bytes.Length)
            throw new InvalidDataException("Cooperative datagram must contain exactly one complete frame");
        return frame;
    }

    public static byte[] Encode(CoopFrame frame, CoopFrameDirection direction)
    {
        ArgumentNullException.ThrowIfNull(frame);
        bool client = direction == CoopFrameDirection.ClientToServer;
        int header = client ? ClientHeaderLength : ServerHeaderLength;
        int commandHeader = client ? 3 : 4;
        if (frame.Commands.Count is < 1 or > MaxCommands) throw new ArgumentException("Invalid cooperative command count");
        int length = 0;
        foreach (var command in frame.Commands)
        {
            if (command.Body.Length > MaxPayloadLength - commandHeader) throw new ArgumentException("Oversized cooperative command");
            length = checked(length + commandHeader + command.Body.Length);
            if (length > MaxPayloadLength) throw new ArgumentException("Oversized cooperative frame");
        }
        var bytes = new byte[header + length];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)length);
        if (client) { bytes[2] = frame.PlayerIndex; BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(3), frame.Code); }
        int offset = header;
        foreach (var command in frame.Commands)
        {
            int size = commandHeader + command.Body.Length;
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), (ushort)size);
            if (!client) bytes[offset + 2] = command.Subject;
            bytes[offset + commandHeader - 1] = command.Type;
            command.Body.Span.CopyTo(bytes.AsSpan(offset + commandHeader));
            offset += size;
        }
        return bytes;
    }
}

/// <summary>Bounded TCP fragmentation/coalescing buffer. Invalid input poisons the stream; never resynchronizes by guessing.</summary>
public sealed class CoopFrameBuffer
{
    private readonly byte[] _buffer = new byte[2 * (CoopFrameCodec.MaxPayloadLength + CoopFrameCodec.ClientHeaderLength)];
    private int _length;
    private bool _invalid;
    public bool HasPartialFrame => _length != 0;

    public IReadOnlyList<CoopFrame> Feed(ReadOnlySpan<byte> bytes, CoopFrameDirection direction)
    {
        if (_invalid) throw new InvalidDataException("Cooperative stream is invalid");
        try
        {
            if (bytes.Length > _buffer.Length - _length) throw new InvalidDataException("Cooperative stream buffer limit exceeded");
            bytes.CopyTo(_buffer.AsSpan(_length)); _length += bytes.Length;
            int offset = 0;
            var frames = new List<CoopFrame>();
            while (CoopFrameCodec.TryDecode(_buffer.AsSpan(offset, _length - offset), direction, out var frame, out int consumed))
            { frames.Add(frame); offset += consumed; }
            if (offset > 0) { _buffer.AsSpan(offset, _length - offset).CopyTo(_buffer); _length -= offset; }
            return frames;
        }
        catch (InvalidDataException) { _invalid = true; throw; }
    }
}

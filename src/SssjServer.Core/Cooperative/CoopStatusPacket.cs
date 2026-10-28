using System.Buffers.Binary;

namespace SssjServer.Core.Cooperative;

public sealed record CoopStatusField(byte Id, uint Version, ReadOnlyMemory<byte> Data);
public sealed record CoopStatusPacket(byte Group, ushort Entity, IReadOnlyList<CoopStatusField> Fields)
{
    /// <summary>Online.MessageSyncStatus (28) body. Parses lengths only; does not trust or apply client state.</summary>
    public static CoopStatusPacket Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 6 || BinaryPrimitives.ReadUInt16LittleEndian(bytes) != bytes.Length)
            throw new InvalidDataException("Invalid cooperative status packet length");
        byte group = bytes[2];
        if (group > 4) throw new InvalidDataException("Unknown cooperative status group");
        ushort entity = BinaryPrimitives.ReadUInt16LittleEndian(bytes[3..]);
        int count = bytes[5], offset = 6;
        var fields = new List<CoopStatusField>(count);
        var ids = new HashSet<byte>();
        for (int i = 0; i < count; i++)
        {
            if (bytes.Length - offset < 7) throw new InvalidDataException("Truncated cooperative status field");
            int length = BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]);
            if (length < 7 || length > bytes.Length - offset) throw new InvalidDataException("Invalid cooperative status field length");
            byte id = bytes[offset + 2];
            if (!ids.Add(id)) throw new InvalidDataException("Duplicate cooperative status field");
            fields.Add(new CoopStatusField(id, BinaryPrimitives.ReadUInt32LittleEndian(bytes[(offset+3)..]),
                bytes.Slice(offset + 7, length - 7).ToArray()));
            offset += length;
        }
        if (offset != bytes.Length) throw new InvalidDataException("Trailing cooperative status bytes");
        return new(group, entity, fields.AsReadOnly());
    }
}

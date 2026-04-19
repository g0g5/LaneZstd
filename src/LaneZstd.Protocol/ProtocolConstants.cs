using System.Buffers.Binary;

namespace LaneZstd.Protocol;

public static class ProtocolConstants
{
    public const ushort Magic = 0x4C5A;
    public const byte Version = 1;
    public const int HeaderSize = 14;

    public static void WriteHeader(
        Span<byte> destination,
        FrameType frameType,
        FrameFlags flags,
        SessionId sessionId,
        ushort rawLength,
        ushort bodyLength)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(destination, Magic);
        destination[2] = Version;
        destination[3] = (byte)frameType;
        destination[4] = (byte)flags;
        destination[5] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(destination[6..], sessionId.Value);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[10..], rawLength);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[12..], bodyLength);
    }
}

using System.Buffers.Binary;

namespace LaneZstd.Protocol;

public readonly record struct LaneZstdFrameHeader(
    ushort Magic,
    byte Version,
    FrameType FrameType,
    FrameFlags Flags,
    byte Reserved,
    SessionId SessionId,
    ushort RawLength,
    ushort BodyLength)
{
    public bool IsCompressed => Flags == FrameFlags.Compressed;

    public static LaneZstdFrameHeader Read(ReadOnlySpan<byte> source)
    {
        return new LaneZstdFrameHeader(
            BinaryPrimitives.ReadUInt16LittleEndian(source),
            source[2],
            (FrameType)source[3],
            (FrameFlags)source[4],
            source[5],
            new SessionId(BinaryPrimitives.ReadUInt32LittleEndian(source[6..])),
            BinaryPrimitives.ReadUInt16LittleEndian(source[10..]),
            BinaryPrimitives.ReadUInt16LittleEndian(source[12..]));
    }
}

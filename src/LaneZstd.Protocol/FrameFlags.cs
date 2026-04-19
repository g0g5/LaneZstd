namespace LaneZstd.Protocol;

[Flags]
public enum FrameFlags : byte
{
    None = 0x00,
    Compressed = 0x01,
}

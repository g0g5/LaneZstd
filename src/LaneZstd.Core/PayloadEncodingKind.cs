namespace LaneZstd.Core;

public enum PayloadEncodingKind
{
    Raw = 0,
    Compressed = 1,
    DroppedOversize = 2,
}

namespace LaneZstd.Core;

public readonly record struct PayloadEncodeResult(
    PayloadEncodingKind Kind,
    int RawLength,
    int EncodedLength,
    int FramedLength)
{
    public bool IsCompressed => Kind == PayloadEncodingKind.Compressed;

    public bool IsOversizeDrop => Kind == PayloadEncodingKind.DroppedOversize;
}

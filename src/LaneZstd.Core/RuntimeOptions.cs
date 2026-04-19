namespace LaneZstd.Core;

public sealed record RuntimeOptions(
    int CompressThreshold,
    int CompressionLevel,
    int MaxPacketSize,
    int StatsIntervalSeconds);

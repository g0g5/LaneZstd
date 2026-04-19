namespace LaneZstd.Core;

public sealed record RuntimeStatsSnapshot(
    long SessionsCreated,
    long SessionsClosed,
    long SessionsTimedOut,
    long ActiveSessions,
    long EdgePacketsIn,
    long EdgePacketsOut,
    long HubPacketsIn,
    long HubPacketsOut,
    long GamePacketsIn,
    long GamePacketsOut,
    long RawFramesOut,
    long CompressedFramesOut,
    long RawBytesIn,
    long FramedBytesOut,
    long OversizeDrop,
    long ProtocolError,
    long DecompressError,
    long UnknownSession,
    long SessionSenderMismatch,
    long PortPoolExhausted,
    int MaxSessions)
{
    public double CompressionRatio => RawBytesIn == 0 ? 1d : (double)FramedBytesOut / RawBytesIn;

    public double CompressionSavings => 1d - CompressionRatio;

    public double SessionUtilization => MaxSessions <= 0 ? 0d : (double)ActiveSessions / MaxSessions;
}

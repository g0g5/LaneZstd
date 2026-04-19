using System.Diagnostics;

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
    long EncodeOperations,
    long EncodeElapsedTicks,
    long DecodeOperations,
    long DecodeElapsedTicks,
    long QueueEnqueued,
    long QueueDequeued,
    long QueueDropped,
    long QueueCompleted,
    long UnknownSession,
    long SessionSenderMismatch,
    long PortPoolExhausted,
    int MaxSessions)
{
    public double CompressionRatio => RawBytesIn == 0 ? 1d : (double)FramedBytesOut / RawBytesIn;

    public double CompressionSavings => 1d - CompressionRatio;

    public double SessionUtilization => MaxSessions <= 0 ? 0d : (double)ActiveSessions / MaxSessions;

    public double EncodeElapsedMilliseconds => EncodeElapsedTicks * 1000d / Stopwatch.Frequency;

    public double DecodeElapsedMilliseconds => DecodeElapsedTicks * 1000d / Stopwatch.Frequency;

    public double EncodeAverageMicroseconds => EncodeOperations == 0 ? 0d : EncodeElapsedTicks * 1_000_000d / Stopwatch.Frequency / EncodeOperations;

    public double DecodeAverageMicroseconds => DecodeOperations == 0 ? 0d : DecodeElapsedTicks * 1_000_000d / Stopwatch.Frequency / DecodeOperations;
}

using System.Globalization;

namespace LaneZstd.Core;

public static class RuntimeStatsReporter
{
    public static Task RunPeriodicAsync(
        string component,
        RuntimeCounters counters,
        int maxSessions,
        int intervalSeconds,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        if (log is null || intervalSeconds <= 0)
        {
            return Task.CompletedTask;
        }

        return RunPeriodicCoreAsync(component, counters, maxSessions, intervalSeconds, log, cancellationToken);
    }

    public static void LogFinal(string component, RuntimeCounters counters, int maxSessions, Action<string>? log)
    {
        if (log is null)
        {
            return;
        }

        log(Format(component, counters.Snapshot(maxSessions), final: true));
    }

    public static string Format(string component, RuntimeStatsSnapshot snapshot, bool final = false)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{component} stats{(final ? " final" : string.Empty)} sessions active={snapshot.ActiveSessions} created={snapshot.SessionsCreated} closed={snapshot.SessionsClosed} timeout={snapshot.SessionsTimedOut} util={snapshot.SessionUtilization:F2} packets edge_in={snapshot.EdgePacketsIn} edge_out={snapshot.EdgePacketsOut} hub_in={snapshot.HubPacketsIn} hub_out={snapshot.HubPacketsOut} game_in={snapshot.GamePacketsIn} game_out={snapshot.GamePacketsOut} raw_out={snapshot.RawFramesOut} zstd_out={snapshot.CompressedFramesOut} raw_bytes_in={snapshot.RawBytesIn} framed_bytes_out={snapshot.FramedBytesOut} drop_oversize={snapshot.OversizeDrop} proto_err={snapshot.ProtocolError} zstd_err={snapshot.DecompressError} unknown_session={snapshot.UnknownSession} sender_mismatch={snapshot.SessionSenderMismatch} pool_exhausted={snapshot.PortPoolExhausted} ratio={snapshot.CompressionRatio:F2} savings={snapshot.CompressionSavings:F2}");
    }

    private static async Task RunPeriodicCoreAsync(
        string component,
        RuntimeCounters counters,
        int maxSessions,
        int intervalSeconds,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            log(Format(component, counters.Snapshot(maxSessions)));
        }
    }
}

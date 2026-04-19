using System.Text;
using LaneZstd.Core;

namespace LaneZstd.Tests;

public sealed class CoreRuntimePrimitivesTests
{
    [Fact]
    public void RuntimeBufferSizing_ComputesPayloadAndCompressionBounds()
    {
        var sizing = RuntimeBufferSizing.Create(1200);

        Assert.Equal(1200, sizing.MaxPacketSize);
        Assert.Equal(1186, sizing.MaxPayloadSize);
        Assert.True(sizing.MaxCompressedPayloadSize >= sizing.MaxPayloadSize);
        Assert.Equal(sizing.MaxCompressedPayloadSize, sizing.MaxReceiveBufferSize);
    }

    [Fact]
    public void SessionPortPool_AcquiresAndReleasesPortsInRange()
    {
        var pool = new SessionPortPool(new SessionPortRange(40000, 40001));

        Assert.True(pool.TryAcquire(out var first));
        Assert.True(pool.TryAcquire(out var second));
        Assert.False(pool.TryAcquire(out _));
        Assert.Equal(40000, first);
        Assert.Equal(40001, second);
        Assert.True(pool.Release(first));
        Assert.False(pool.Release(first));
        Assert.True(pool.TryAcquire(out var reacquired));
        Assert.Equal(first, reacquired);
    }

    [Fact]
    public void PayloadEncoder_UsesRawForSmallPayloads()
    {
        var runtime = new RuntimeOptions(CompressThreshold: 96, CompressionLevel: 3, MaxPacketSize: 1200, StatsIntervalSeconds: 5);
        var sizing = RuntimeBufferSizing.Create(runtime.MaxPacketSize);
        using var encoder = new PayloadEncoder(runtime, sizing);
        var payload = Encoding.ASCII.GetBytes("small-packet");
        var buffer = new byte[sizing.MaxCompressedPayloadSize];

        var result = encoder.Encode(payload, buffer, out var bytesWritten);

        Assert.Equal(PayloadEncodingKind.Raw, result.Kind);
        Assert.False(result.IsCompressed);
        Assert.Equal(payload.Length, bytesWritten);
        Assert.Equal(payload, buffer[..bytesWritten]);
    }

    [Fact]
    public void PayloadEncoder_UsesCompressedWhenItIsBeneficial()
    {
        var runtime = new RuntimeOptions(CompressThreshold: 32, CompressionLevel: 3, MaxPacketSize: 1200, StatsIntervalSeconds: 5);
        var sizing = RuntimeBufferSizing.Create(runtime.MaxPacketSize);
        using var encoder = new PayloadEncoder(runtime, sizing);
        var payload = Encoding.ASCII.GetBytes(new string('A', 256));
        var buffer = new byte[sizing.MaxCompressedPayloadSize];

        var result = encoder.Encode(payload, buffer, out var bytesWritten);

        Assert.Equal(PayloadEncodingKind.Compressed, result.Kind);
        Assert.True(result.IsCompressed);
        Assert.True(bytesWritten < payload.Length);
        Assert.Equal(bytesWritten, result.EncodedLength);
    }

    [Fact]
    public void PayloadEncoder_FallsBackToRawWhenCompressionIsNotBeneficial()
    {
        var runtime = new RuntimeOptions(CompressThreshold: 1, CompressionLevel: 3, MaxPacketSize: 1200, StatsIntervalSeconds: 5);
        var sizing = RuntimeBufferSizing.Create(runtime.MaxPacketSize);
        using var encoder = new PayloadEncoder(runtime, sizing);
        var payload = Enumerable.Range(0, 256).Select(static value => (byte)value).ToArray();
        var buffer = new byte[sizing.MaxCompressedPayloadSize];

        var result = encoder.Encode(payload, buffer, out var bytesWritten);

        Assert.Equal(PayloadEncodingKind.Raw, result.Kind);
        Assert.False(result.IsCompressed);
        Assert.Equal(payload.Length, bytesWritten);
        Assert.Equal(payload, buffer[..bytesWritten]);
    }

    [Fact]
    public void PayloadEncoder_DropsOversizePayloadWhenRawFrameWouldExceedCeiling()
    {
        var runtime = new RuntimeOptions(CompressThreshold: 4096, CompressionLevel: 3, MaxPacketSize: 128, StatsIntervalSeconds: 5);
        var sizing = RuntimeBufferSizing.Create(runtime.MaxPacketSize);
        using var encoder = new PayloadEncoder(runtime, sizing);
        var payload = Enumerable.Repeat((byte)1, sizing.MaxPayloadSize + 1).ToArray();
        var buffer = new byte[sizing.MaxCompressedPayloadSize];

        var result = encoder.Encode(payload, buffer, out var bytesWritten);

        Assert.Equal(PayloadEncodingKind.DroppedOversize, result.Kind);
        Assert.True(result.IsOversizeDrop);
        Assert.Equal(0, bytesWritten);
    }

    [Fact]
    public void RuntimeCounters_SnapshotIncludesDerivedMetrics()
    {
        var counters = new RuntimeCounters();
        counters.Increment(RuntimeCounter.SessionsCreated, 2);
        counters.Increment(RuntimeCounter.RawFramesOut, 3);
        counters.Increment(RuntimeCounter.RawBytesIn, 200);
        counters.Increment(RuntimeCounter.FramedBytesOut, 150);
        counters.ChangeActiveSessions(1);

        var snapshot = counters.Snapshot(maxSessions: 4);

        Assert.Equal(2, snapshot.SessionsCreated);
        Assert.Equal(1, snapshot.ActiveSessions);
        Assert.Equal(0.75d, snapshot.CompressionRatio, 3);
        Assert.Equal(0.25d, snapshot.CompressionSavings, 3);
        Assert.Equal(0.25d, snapshot.SessionUtilization, 3);
    }

    [Fact]
    public void RuntimeStatsReporter_FormatsSingleLineStatsOutput()
    {
        var snapshot = new RuntimeStatsSnapshot(
            SessionsCreated: 3,
            SessionsClosed: 1,
            SessionsTimedOut: 2,
            ActiveSessions: 4,
            EdgePacketsIn: 10,
            EdgePacketsOut: 11,
            HubPacketsIn: 12,
            HubPacketsOut: 13,
            GamePacketsIn: 14,
            GamePacketsOut: 15,
            RawFramesOut: 16,
            CompressedFramesOut: 17,
            RawBytesIn: 200,
            FramedBytesOut: 150,
            OversizeDrop: 5,
            ProtocolError: 6,
            DecompressError: 7,
            UnknownSession: 8,
            SessionSenderMismatch: 9,
            PortPoolExhausted: 10,
            MaxSessions: 8);

        var line = RuntimeStatsReporter.Format("hub", snapshot, final: true);

        Assert.Contains("hub stats final", line);
        Assert.Contains("active=4", line);
        Assert.Contains("created=3", line);
        Assert.Contains("closed=1", line);
        Assert.Contains("timeout=2", line);
        Assert.Contains("util=0.50", line);
        Assert.Contains("edge_in=10", line);
        Assert.Contains("hub_out=13", line);
        Assert.Contains("game_out=15", line);
        Assert.Contains("raw_out=16", line);
        Assert.Contains("zstd_out=17", line);
        Assert.Contains("raw_bytes_in=200", line);
        Assert.Contains("framed_bytes_out=150", line);
        Assert.Contains("drop_oversize=5", line);
        Assert.Contains("proto_err=6", line);
        Assert.Contains("zstd_err=7", line);
        Assert.Contains("unknown_session=8", line);
        Assert.Contains("sender_mismatch=9", line);
        Assert.Contains("pool_exhausted=10", line);
        Assert.Contains("ratio=0.75", line);
        Assert.Contains("savings=0.25", line);
    }
}

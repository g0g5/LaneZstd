using LaneZstd.Cli;
using LaneZstd.Core;
using Xunit.Abstractions;

namespace LaneZstd.Tests;

public sealed class RealisticTrafficIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task TrafficBenchRunner_RelaysBidirectionalJsonTrafficAndReportsStats()
    {
        var config = new BenchConfig(
            Duration: TimeSpan.FromSeconds(1.5),
            Warmup: TimeSpan.FromSeconds(0.5),
            MessagesPerSecond: 40,
            AveragePayloadBytes: 700,
            MinPayloadBytes: 50,
            MaxPayloadBytes: 1200,
            ValidationMode: BenchValidationMode.Integrity,
            Seed: 424242,
            OutputFormat: "text",
            Runtime: new RuntimeOptions(
                CompressThreshold: 96,
                CompressionLevel: 3,
                MaxPacketSize: 1400,
                StatsIntervalSeconds: 0,
                ReceiveQueueCapacity: 256,
                ReceiveWorkerCount: 1));

        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var result = await TrafficBenchRunner.RunAsync(config, output.WriteLine, cancellationSource.Token);

        output.WriteLine(result.FormatSummary("text"));

        Assert.True(result.IsSuccessful);

        Assert.True(result.EdgeToHub.MessagesSent > 0);
        Assert.Equal(result.EdgeToHub.MessagesSent, result.EdgeToHub.MessagesReceived);
        Assert.Equal(result.EdgeToHub.MessagesSent, result.EdgeToHub.SendAttempts);
        Assert.Equal(result.EdgeToHub.MessagesSent, result.EdgeToHub.SocketSendCompleted);
        Assert.True(result.EdgeToHub.ReceiverDatagrams > 0);
        Assert.True(result.EdgeToHub.SourceIngressPackets > 0);
        Assert.True(result.EdgeToHub.DestinationTunnelInPackets > 0);
        Assert.True(result.EdgeToHub.RawBytes > 0);
        Assert.True(result.EdgeToHub.FramedBytes > 0);
        Assert.True(result.EdgeToHub.CompressedFrames > 0);
        Assert.Equal(0, result.EdgeToHub.Lost);
        Assert.Equal(0, result.EdgeToHub.Duplicated);
        Assert.Equal(0, result.EdgeToHub.Corrupted);
        Assert.Equal(0, result.EdgeToHub.Corruption.JsonInvalid);
        Assert.Equal(0, result.EdgeToHub.Corruption.DirectionMismatch);
        Assert.Equal(0, result.EdgeToHub.Corruption.ChecksumMismatch);

        Assert.True(result.HubToEdge.MessagesSent > 0);
        Assert.Equal(result.HubToEdge.MessagesSent, result.HubToEdge.MessagesReceived);
        Assert.Equal(result.HubToEdge.MessagesSent, result.HubToEdge.SendAttempts);
        Assert.Equal(result.HubToEdge.MessagesSent, result.HubToEdge.SocketSendCompleted);
        Assert.True(result.HubToEdge.ReceiverDatagrams > 0);
        Assert.True(result.HubToEdge.SourceIngressPackets > 0);
        Assert.True(result.HubToEdge.DestinationTunnelInPackets > 0);
        Assert.True(result.HubToEdge.RawBytes > 0);
        Assert.True(result.HubToEdge.FramedBytes > 0);
        Assert.True(result.HubToEdge.CompressedFrames > 0);
        Assert.Equal(0, result.HubToEdge.Lost);
        Assert.Equal(0, result.HubToEdge.Duplicated);
        Assert.Equal(0, result.HubToEdge.Corrupted);
        Assert.Equal(0, result.HubToEdge.Corruption.JsonInvalid);
        Assert.Equal(0, result.HubToEdge.Corruption.DirectionMismatch);
        Assert.Equal(0, result.HubToEdge.Corruption.ChecksumMismatch);
    }

    [Fact]
    public async Task TrafficBenchRunner_DefaultPacketBudgetStaysAtZeroIntegrityErrors()
    {
        var config = new BenchConfig(
            Duration: TimeSpan.FromSeconds(2),
            Warmup: TimeSpan.FromSeconds(0.5),
            MessagesPerSecond: 200,
            AveragePayloadBytes: 700,
            MinPayloadBytes: 50,
            MaxPayloadBytes: 1186,
            ValidationMode: BenchValidationMode.Integrity,
            Seed: 8675309,
            OutputFormat: "text",
            Runtime: new RuntimeOptions(
                CompressThreshold: 96,
                CompressionLevel: 3,
                MaxPacketSize: 1200,
                StatsIntervalSeconds: 0,
                ReceiveQueueCapacity: 256,
                ReceiveWorkerCount: 1));

        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var result = await TrafficBenchRunner.RunAsync(config, output.WriteLine, cancellationSource.Token);

        output.WriteLine(result.FormatSummary("text"));

        Assert.True(result.IsSuccessful);
        Assert.Equal(0, result.EdgeToHub.Lost);
        Assert.Equal(0, result.EdgeToHub.Duplicated);
        Assert.Equal(0, result.EdgeToHub.Corrupted);
        Assert.Equal(0, result.HubToEdge.Lost);
        Assert.Equal(0, result.HubToEdge.Duplicated);
        Assert.Equal(0, result.HubToEdge.Corrupted);
    }

    [Fact]
    public async Task TrafficBenchRunner_NoneValidationMode_RunsWithoutIntegrityChecks()
    {
        var config = new BenchConfig(
            Duration: TimeSpan.FromSeconds(1.5),
            Warmup: TimeSpan.FromSeconds(0.5),
            MessagesPerSecond: 40,
            AveragePayloadBytes: 700,
            MinPayloadBytes: 50,
            MaxPayloadBytes: 1186,
            ValidationMode: BenchValidationMode.None,
            Seed: 20260419,
            OutputFormat: "text",
            Runtime: new RuntimeOptions(
                CompressThreshold: 96,
                CompressionLevel: 3,
                MaxPacketSize: 1200,
                StatsIntervalSeconds: 0,
                ReceiveQueueCapacity: 256,
                ReceiveWorkerCount: 1));

        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var result = await TrafficBenchRunner.RunAsync(config, output.WriteLine, cancellationSource.Token);

        output.WriteLine(result.FormatSummary("text"));

        Assert.True(result.EdgeToHub.MessagesReceived > 0);
        Assert.True(result.HubToEdge.MessagesReceived > 0);
        Assert.Equal(0, result.EdgeToHub.Corrupted);
        Assert.Equal(0, result.HubToEdge.Corrupted);
    }
}

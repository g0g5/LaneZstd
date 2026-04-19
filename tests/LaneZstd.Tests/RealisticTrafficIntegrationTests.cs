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
            Seed: 424242,
            OutputFormat: "text",
            Runtime: new RuntimeOptions(
                CompressThreshold: 96,
                CompressionLevel: 3,
                MaxPacketSize: 1400,
                StatsIntervalSeconds: 0));

        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var result = await TrafficBenchRunner.RunAsync(config, output.WriteLine, cancellationSource.Token);

        output.WriteLine(result.FormatSummary("text"));

        Assert.True(result.IsSuccessful);

        Assert.True(result.EdgeToHub.MessagesSent > 0);
        Assert.Equal(result.EdgeToHub.MessagesSent, result.EdgeToHub.MessagesReceived);
        Assert.True(result.EdgeToHub.RawBytes > 0);
        Assert.True(result.EdgeToHub.FramedBytes > 0);
        Assert.True(result.EdgeToHub.CompressedFrames > 0);
        Assert.Equal(0, result.EdgeToHub.Lost);
        Assert.Equal(0, result.EdgeToHub.Duplicated);
        Assert.Equal(0, result.EdgeToHub.Corrupted);

        Assert.True(result.HubToEdge.MessagesSent > 0);
        Assert.Equal(result.HubToEdge.MessagesSent, result.HubToEdge.MessagesReceived);
        Assert.True(result.HubToEdge.RawBytes > 0);
        Assert.True(result.HubToEdge.FramedBytes > 0);
        Assert.True(result.HubToEdge.CompressedFrames > 0);
        Assert.Equal(0, result.HubToEdge.Lost);
        Assert.Equal(0, result.HubToEdge.Duplicated);
        Assert.Equal(0, result.HubToEdge.Corrupted);
    }
}

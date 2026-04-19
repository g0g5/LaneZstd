using LaneZstd.Cli;
using LaneZstd.Core;

namespace LaneZstd.Tests;

public class CliTests
{
    [Fact]
    public void EdgeCommand_MapsConfigAndDefaults()
    {
        var cli = CliApplication.BuildCli();
        var parseResult = cli.RootCommand.Parse([
            "edge",
            "--hub", "198.51.100.10:38441",
            "--game-listen", "127.0.0.1:38443",
            "--verbose",
        ]);

        var success = cli.TryGetEdgeConfig(parseResult, out var config, out var errors);

        Assert.True(success);
        Assert.Empty(errors);
        Assert.NotNull(config);
        Assert.Equal(new UdpEndpoint(System.Net.IPAddress.Any, 38441), config.BindEndpoint);
        Assert.Equal(new UdpEndpoint(System.Net.IPAddress.Parse("198.51.100.10"), 38441), config.HubEndpoint);
        Assert.Equal(new UdpEndpoint(System.Net.IPAddress.Loopback, 38443), config.GameListenEndpoint);
        Assert.Equal(96, config.Runtime.CompressThreshold);
        Assert.Equal(3, config.Runtime.CompressionLevel);
        Assert.Equal(1200, config.Runtime.MaxPacketSize);
        Assert.Equal(5, config.Runtime.StatsIntervalSeconds);
        Assert.True(cli.IsVerbose(parseResult));
    }

    [Fact]
    public void HubCommand_MapsConfigAndDerivesDefaultMaxSessions()
    {
        var cli = CliApplication.BuildCli();
        var parseResult = cli.RootCommand.Parse([
            "hub",
            "--game", "127.0.0.1:16261",
            "--session-port-range", "40000-40002",
        ]);

        var success = cli.TryGetHubConfig(parseResult, out var config, out var errors);

        Assert.True(success);
        Assert.Empty(errors);
        Assert.NotNull(config);
        Assert.Equal(new UdpEndpoint(System.Net.IPAddress.Any, 38441), config.BindEndpoint);
        Assert.Equal(new UdpEndpoint(System.Net.IPAddress.Loopback, 16261), config.GameEndpoint);
        Assert.Equal(new SessionPortRange(40000, 40002), config.SessionPortRange);
        Assert.Equal(30, config.SessionIdleTimeoutSeconds);
        Assert.Equal(3, config.MaxSessions);
        Assert.Equal(96, config.Runtime.CompressThreshold);
        Assert.Equal(3, config.Runtime.CompressionLevel);
        Assert.Equal(1200, config.Runtime.MaxPacketSize);
        Assert.Equal(5, config.Runtime.StatsIntervalSeconds);
        Assert.False(cli.IsVerbose(parseResult));
    }

    [Fact]
    public void VerboseOption_IsAvailableAsRecursiveGlobalOption()
    {
        var cli = CliApplication.BuildCli();
        var parseResult = cli.RootCommand.Parse([
            "--verbose",
            "edge",
            "--hub", "198.51.100.10:38441",
            "--game-listen", "127.0.0.1:38443",
        ]);

        var success = cli.TryGetEdgeConfig(parseResult, out var config, out var errors);

        Assert.True(success);
        Assert.Empty(errors);
        Assert.NotNull(config);
        Assert.True(cli.IsVerbose(parseResult));
    }

    [Fact]
    public void EdgeCommand_RejectsDuplicateEndpoints()
    {
        var cli = CliApplication.BuildCli();
        var parseResult = cli.RootCommand.Parse([
            "edge",
            "--bind", "127.0.0.1:38443",
            "--hub", "198.51.100.10:38441",
            "--game-listen", "127.0.0.1:38443",
        ]);

        var success = cli.TryGetEdgeConfig(parseResult, out _, out var errors);

        Assert.False(success);
        Assert.Contains("--bind and --game-listen must differ.", errors);
    }

    [Fact]
    public void EdgeCommand_RejectsWildcardGameListen()
    {
        var cli = CliApplication.BuildCli();
        var parseResult = cli.RootCommand.Parse([
            "edge",
            "--hub", "198.51.100.10:38441",
            "--game-listen", "0.0.0.0:38443",
        ]);

        var success = cli.TryGetEdgeConfig(parseResult, out _, out var errors);

        Assert.False(success);
        Assert.Contains("--game-listen must not use a wildcard address.", errors);
    }

    [Fact]
    public void HubCommand_RejectsOverlappingBindAndOversizedSessionCount()
    {
        var cli = CliApplication.BuildCli();
        var parseResult = cli.RootCommand.Parse([
            "hub",
            "--bind", "0.0.0.0:40001",
            "--game", "127.0.0.1:16261",
            "--session-port-range", "40000-40002",
            "--max-sessions", "4",
        ]);

        var success = cli.TryGetHubConfig(parseResult, out _, out var errors);

        Assert.False(success);
        Assert.Contains("--bind port must not overlap --session-port-range.", errors);
        Assert.Contains("--max-sessions must not exceed the size of --session-port-range.", errors);
    }

    [Fact]
    public void HubCommand_RejectsExplicitZeroMaxSessions()
    {
        var cli = CliApplication.BuildCli();
        var parseResult = cli.RootCommand.Parse([
            "hub",
            "--game", "127.0.0.1:16261",
            "--session-port-range", "40000-40002",
            "--max-sessions", "0",
        ]);

        var success = cli.TryGetHubConfig(parseResult, out _, out var errors);

        Assert.False(success);
        Assert.Contains("--max-sessions must be at least 1.", errors);
    }

    [Fact]
    public void SharedValidation_RejectsInvalidEndpointAndRuntimeLimits()
    {
        var cli = CliApplication.BuildCli();
        var parseResult = cli.RootCommand.Parse([
            "edge",
            "--hub", "not-an-endpoint",
            "--game-listen", "127.0.0.1:38443",
            "--compress-threshold", "-1",
            "--compression-level", "23",
            "--max-packet-size", "127",
            "--stats-interval", "-1",
        ]);

        var success = cli.TryGetEdgeConfig(parseResult, out _, out var errors);

        Assert.False(success);
        Assert.Contains("--hub must be a valid ip:port endpoint.", errors);
        Assert.Contains("--compress-threshold must be zero or greater.", errors);
        Assert.Contains("--compression-level must be between 1 and 22.", errors);
        Assert.Contains("--max-packet-size must be at least 128.", errors);
        Assert.Contains("--stats-interval must be zero or greater.", errors);
    }

    [Fact]
    public void BenchCommand_MapsConfigAndDefaults()
    {
        var cli = CliApplication.BuildCli();
        var parseResult = cli.RootCommand.Parse([
            "bench",
            "--duration-seconds", "15",
            "--warmup-seconds", "2",
            "--messages-per-second", "120",
            "--avg-payload-bytes", "700",
            "--min-payload-bytes", "50",
            "--max-payload-bytes", "1350",
            "--seed", "1234",
            "--output", "json",
            "--compress-threshold", "80",
            "--compression-level", "5",
            "--max-packet-size", "1400",
            "--stats-interval", "0",
        ]);

        var success = cli.TryGetBenchConfig(parseResult, out var config, out var errors);

        Assert.True(success);
        Assert.Empty(errors);
        Assert.NotNull(config);
        Assert.Equal(TimeSpan.FromSeconds(15), config.Duration);
        Assert.Equal(TimeSpan.FromSeconds(2), config.Warmup);
        Assert.Equal(120, config.MessagesPerSecond);
        Assert.Equal(700, config.AveragePayloadBytes);
        Assert.Equal(50, config.MinPayloadBytes);
        Assert.Equal(1350, config.MaxPayloadBytes);
        Assert.Equal(1234, config.Seed);
        Assert.Equal("json", config.OutputFormat);
        Assert.Equal(80, config.Runtime.CompressThreshold);
        Assert.Equal(5, config.Runtime.CompressionLevel);
        Assert.Equal(1400, config.Runtime.MaxPacketSize);
        Assert.Equal(0, config.Runtime.StatsIntervalSeconds);
    }
}

using System.CommandLine;
using System.CommandLine.Parsing;
using LaneZstd.Core;

namespace LaneZstd.Cli;

internal static class CliDefaults
{
    public const string BindEndpointText = "0.0.0.0:38441";
    public const int CompressThreshold = 96;
    public const int CompressionLevel = 3;
    public const int MaxPacketSize = 1200;
    public const int StatsIntervalSeconds = 5;
    public const int SessionIdleTimeoutSeconds = 30;
    public const int UnspecifiedMaxSessions = -1;
}

public static class CliApplication
{
    public static RootCommand BuildRootCommand() => BuildCli().RootCommand;

    public static CliDefinition BuildCli()
    {
        var verboseOption = new Option<bool>("--verbose", "-v")
        {
            Description = "Enables packet-level debug logging.",
            Recursive = true,
        };

        var edgeBindOption = new Option<string>("--bind")
        {
            Description = "Local tunnel bind endpoint.",
        };
        var edgeHubOption = new Option<string>("--hub")
        {
            Description = "Remote hub endpoint.",
            Required = true,
        };
        var edgeGameListenOption = new Option<string>("--game-listen")
        {
            Description = "Local game-facing endpoint.",
            Required = true,
        };
        var edgeCompressThresholdOption = new Option<int>("--compress-threshold")
        {
            Description = "Compression threshold in bytes.",
        };
        var edgeCompressionLevelOption = new Option<int>("--compression-level")
        {
            Description = "Zstd compression level.",
        };
        var edgeMaxPacketSizeOption = new Option<int>("--max-packet-size")
        {
            Description = "Maximum framed UDP packet size.",
        };
        var edgeStatsIntervalOption = new Option<int>("--stats-interval")
        {
            Description = "Stats interval in seconds. Zero disables periodic stats.",
        };

        var edgeCommand = new Command("edge", "Run edge node")
        {
            edgeBindOption,
            edgeHubOption,
            edgeGameListenOption,
            edgeCompressThresholdOption,
            edgeCompressionLevelOption,
            edgeMaxPacketSizeOption,
            edgeStatsIntervalOption,
        };

        edgeCommand.Validators.Add(result =>
        {
            ValidateEndpointOption(result, edgeBindOption, "--bind", allowWildcard: true, fallbackValue: CliDefaults.BindEndpointText);
            ValidateEndpointOption(result, edgeHubOption, "--hub", allowWildcard: false);
            ValidateEndpointOption(result, edgeGameListenOption, "--game-listen", allowWildcard: false);
            ValidateSharedRuntimeOptions(result, edgeCompressThresholdOption, edgeCompressionLevelOption, edgeMaxPacketSizeOption, edgeStatsIntervalOption);

            if (!TryGetEndpoint(result, edgeBindOption, out var bindEndpoint, CliDefaults.BindEndpointText) ||
                !TryGetEndpoint(result, edgeGameListenOption, out var gameListenEndpoint))
            {
                return;
            }

            if (bindEndpoint == gameListenEndpoint)
            {
                result.AddError("--bind and --game-listen must differ.");
            }
        });

        var hubBindOption = new Option<string>("--bind")
        {
            Description = "Public hub bind endpoint.",
        };
        var hubGameOption = new Option<string>("--game")
        {
            Description = "Local game server endpoint.",
            Required = true,
        };
        var sessionPortRangeOption = new Option<string>("--session-port-range")
        {
            Description = "Per-session local UDP port range.",
            Required = true,
        };
        var sessionIdleTimeoutOption = new Option<int>("--session-idle-timeout")
        {
            Description = "Session idle timeout in seconds.",
        };
        var maxSessionsOption = new Option<int>("--max-sessions")
        {
            Description = "Maximum concurrent sessions.",
        };
        var hubCompressThresholdOption = new Option<int>("--compress-threshold")
        {
            Description = "Compression threshold in bytes.",
        };
        var hubCompressionLevelOption = new Option<int>("--compression-level")
        {
            Description = "Zstd compression level.",
        };
        var hubMaxPacketSizeOption = new Option<int>("--max-packet-size")
        {
            Description = "Maximum framed UDP packet size.",
        };
        var hubStatsIntervalOption = new Option<int>("--stats-interval")
        {
            Description = "Stats interval in seconds. Zero disables periodic stats.",
        };

        var hubCommand = new Command("hub", "Run hub node")
        {
            hubBindOption,
            hubGameOption,
            sessionPortRangeOption,
            sessionIdleTimeoutOption,
            maxSessionsOption,
            hubCompressThresholdOption,
            hubCompressionLevelOption,
            hubMaxPacketSizeOption,
            hubStatsIntervalOption,
        };

        hubCommand.Validators.Add(result =>
        {
            ValidateEndpointOption(result, hubBindOption, "--bind", allowWildcard: true, fallbackValue: CliDefaults.BindEndpointText);
            ValidateEndpointOption(result, hubGameOption, "--game", allowWildcard: false);
            ValidateSessionPortRangeOption(result, sessionPortRangeOption);
            ValidateSharedRuntimeOptions(result, hubCompressThresholdOption, hubCompressionLevelOption, hubMaxPacketSizeOption, hubStatsIntervalOption);

            var sessionIdleTimeout = GetOptionValue(result, sessionIdleTimeoutOption, CliDefaults.SessionIdleTimeoutSeconds);
            if (sessionIdleTimeout < 1)
            {
                result.AddError("--session-idle-timeout must be at least 1.");
            }

            var maxSessions = GetOptionValue(result, maxSessionsOption, CliDefaults.UnspecifiedMaxSessions);
            if (maxSessions != CliDefaults.UnspecifiedMaxSessions && maxSessions < 1)
            {
                result.AddError("--max-sessions must be at least 1.");
            }

            if (!TryGetEndpoint(result, hubBindOption, out var bindEndpoint, CliDefaults.BindEndpointText) ||
                !TryGetSessionPortRange(result, sessionPortRangeOption, out var portRange))
            {
                return;
            }

            if (portRange.Contains(bindEndpoint.Port))
            {
                result.AddError("--bind port must not overlap --session-port-range.");
            }

            if (maxSessions == CliDefaults.UnspecifiedMaxSessions)
            {
                return;
            }

            if (maxSessions > portRange.Count)
            {
                result.AddError("--max-sessions must not exceed the size of --session-port-range.");
            }
        });

        var rootCommand = new RootCommand("LaneZstd UDP hub runtime")
        {
            verboseOption,
            edgeCommand,
            hubCommand,
        };

        return new CliDefinition(
            rootCommand,
            edgeCommand,
            hubCommand,
            verboseOption,
            edgeBindOption,
            edgeHubOption,
            edgeGameListenOption,
            edgeCompressThresholdOption,
            edgeCompressionLevelOption,
            edgeMaxPacketSizeOption,
            edgeStatsIntervalOption,
            hubBindOption,
            hubGameOption,
            sessionPortRangeOption,
            sessionIdleTimeoutOption,
            maxSessionsOption,
            hubCompressThresholdOption,
            hubCompressionLevelOption,
            hubMaxPacketSizeOption,
            hubStatsIntervalOption);
    }

    private static void ValidateEndpointOption(CommandResult result, Option<string> option, string alias, bool allowWildcard, string? fallbackValue = null)
    {
        if (!TryGetEndpoint(result, option, out var endpoint, fallbackValue))
        {
            result.AddError($"{alias} must be a valid ip:port endpoint.");
            return;
        }

        if (!allowWildcard && endpoint.IsWildcard)
        {
            result.AddError($"{alias} must not use a wildcard address.");
        }
    }

    private static void ValidateSessionPortRangeOption(CommandResult result, Option<string> option)
    {
        if (!TryGetSessionPortRange(result, option, out _))
        {
            result.AddError("--session-port-range must be a valid start-end range within 1..65535.");
        }
    }

    private static void ValidateSharedRuntimeOptions(
        CommandResult result,
        Option<int> compressThresholdOption,
        Option<int> compressionLevelOption,
        Option<int> maxPacketSizeOption,
        Option<int> statsIntervalOption)
    {
        if (GetOptionValue(result, compressThresholdOption, CliDefaults.CompressThreshold) < 0)
        {
            result.AddError("--compress-threshold must be zero or greater.");
        }

        var compressionLevel = GetOptionValue(result, compressionLevelOption, CliDefaults.CompressionLevel);
        if (compressionLevel is < 1 or > 22)
        {
            result.AddError("--compression-level must be between 1 and 22.");
        }

        if (GetOptionValue(result, maxPacketSizeOption, CliDefaults.MaxPacketSize) < 128)
        {
            result.AddError("--max-packet-size must be at least 128.");
        }

        if (GetOptionValue(result, statsIntervalOption, CliDefaults.StatsIntervalSeconds) < 0)
        {
            result.AddError("--stats-interval must be zero or greater.");
        }
    }

    private static bool TryGetEndpoint(CommandResult result, Option<string> option, out UdpEndpoint endpoint, string? fallbackValue = null) =>
        UdpEndpoint.TryParse(result.GetResult(option) is null ? fallbackValue : result.GetValue(option), out endpoint);

    private static bool TryGetSessionPortRange(CommandResult result, Option<string> option, out SessionPortRange portRange) =>
        SessionPortRange.TryParse(result.GetValue(option), out portRange);

    private static T GetOptionValue<T>(CommandResult result, Option<T> option, T fallbackValue) =>
        result.GetResult(option) is null ? fallbackValue : result.GetValue(option)!;
}

public sealed class CliDefinition(
    RootCommand rootCommand,
    Command edgeCommand,
    Command hubCommand,
    Option<bool> verboseOption,
    Option<string> edgeBindOption,
    Option<string> edgeHubOption,
    Option<string> edgeGameListenOption,
    Option<int> edgeCompressThresholdOption,
    Option<int> edgeCompressionLevelOption,
    Option<int> edgeMaxPacketSizeOption,
    Option<int> edgeStatsIntervalOption,
    Option<string> hubBindOption,
    Option<string> hubGameOption,
    Option<string> sessionPortRangeOption,
    Option<int> sessionIdleTimeoutOption,
    Option<int> maxSessionsOption,
    Option<int> hubCompressThresholdOption,
    Option<int> hubCompressionLevelOption,
    Option<int> hubMaxPacketSizeOption,
    Option<int> hubStatsIntervalOption)
{
    public RootCommand RootCommand { get; } = rootCommand;

    public bool IsVerbose(ParseResult parseResult) => parseResult.GetValue(verboseOption);

    public bool TryGetEdgeConfig(ParseResult parseResult, out EdgeConfig? config, out IReadOnlyList<string> errors)
    {
        config = null;
        errors = GetErrors(parseResult);
        if (errors.Count > 0 || parseResult.CommandResult.Command != edgeCommand)
        {
            return false;
        }

        config = new EdgeConfig(
            BindEndpoint: ParseEndpoint(parseResult, edgeBindOption, CliDefaults.BindEndpointText),
            HubEndpoint: ParseEndpoint(parseResult, edgeHubOption),
            GameListenEndpoint: ParseEndpoint(parseResult, edgeGameListenOption),
            Runtime: new RuntimeOptions(
                GetOptionValue(parseResult, edgeCompressThresholdOption, CliDefaults.CompressThreshold),
                GetOptionValue(parseResult, edgeCompressionLevelOption, CliDefaults.CompressionLevel),
                GetOptionValue(parseResult, edgeMaxPacketSizeOption, CliDefaults.MaxPacketSize),
                GetOptionValue(parseResult, edgeStatsIntervalOption, CliDefaults.StatsIntervalSeconds)));

        return true;
    }

    public bool TryGetHubConfig(ParseResult parseResult, out HubConfig? config, out IReadOnlyList<string> errors)
    {
        config = null;
        errors = GetErrors(parseResult);
        if (errors.Count > 0 || parseResult.CommandResult.Command != hubCommand)
        {
            return false;
        }

        var portRange = ParseSessionPortRange(parseResult, sessionPortRangeOption);
        var maxSessions = GetOptionValue(parseResult, maxSessionsOption, CliDefaults.UnspecifiedMaxSessions);
        if (maxSessions == CliDefaults.UnspecifiedMaxSessions)
        {
            maxSessions = portRange.Count;
        }

        config = new HubConfig(
            BindEndpoint: ParseEndpoint(parseResult, hubBindOption, CliDefaults.BindEndpointText),
            GameEndpoint: ParseEndpoint(parseResult, hubGameOption),
            SessionPortRange: portRange,
            SessionIdleTimeoutSeconds: GetOptionValue(parseResult, sessionIdleTimeoutOption, CliDefaults.SessionIdleTimeoutSeconds),
            MaxSessions: maxSessions,
            Runtime: new RuntimeOptions(
                GetOptionValue(parseResult, hubCompressThresholdOption, CliDefaults.CompressThreshold),
                GetOptionValue(parseResult, hubCompressionLevelOption, CliDefaults.CompressionLevel),
                GetOptionValue(parseResult, hubMaxPacketSizeOption, CliDefaults.MaxPacketSize),
                GetOptionValue(parseResult, hubStatsIntervalOption, CliDefaults.StatsIntervalSeconds)));

        return true;
    }

    private static List<string> GetErrors(ParseResult parseResult) => parseResult.Errors.Select(error => error.Message).ToList();

    private static UdpEndpoint ParseEndpoint(ParseResult parseResult, Option<string> option, string? fallbackValue = null)
    {
        _ = UdpEndpoint.TryParse(parseResult.GetResult(option) is null ? fallbackValue : parseResult.GetValue(option), out var endpoint);
        return endpoint;
    }

    private static SessionPortRange ParseSessionPortRange(ParseResult parseResult, Option<string> option)
    {
        _ = SessionPortRange.TryParse(parseResult.GetValue(option), out var portRange);
        return portRange;
    }

    private static T GetOptionValue<T>(ParseResult parseResult, Option<T> option, T fallbackValue) =>
        parseResult.GetResult(option) is null ? fallbackValue : parseResult.GetValue(option)!;
}

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var cli = CliApplication.BuildCli();
        var parseResult = cli.RootCommand.Parse(args);

        if (parseResult.CommandResult.Command == cli.RootCommand || parseResult.Errors.Count > 0)
        {
            return parseResult.Invoke();
        }

        if (cli.TryGetEdgeConfig(parseResult, out var edgeConfig, out _))
        {
            using var cancellationSource = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellationSource.Cancel();
            };

            var runtime = new EdgeRuntime(edgeConfig!, log: Console.WriteLine, verbose: cli.IsVerbose(parseResult));
            await runtime.RunAsync(cancellationSource.Token);
            return 0;
        }

        if (cli.TryGetHubConfig(parseResult, out var hubConfig, out _))
        {
            using var cancellationSource = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellationSource.Cancel();
            };

            var runtime = new HubRuntime(hubConfig!, log: Console.WriteLine, verbose: cli.IsVerbose(parseResult));
            await runtime.RunAsync(cancellationSource.Token);
            return 0;
        }

        return parseResult.Invoke();
    }
}

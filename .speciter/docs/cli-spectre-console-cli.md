# Spectre.Console.Cli for a .NET 10 UDP Middleware CLI

Sources:
- https://spectreconsole.net/cli/how-to/configuring-commandapp-and-commands
- https://spectreconsole.net/cli/how-to/defining-commands-and-arguments
- https://spectreconsole.net/cli/how-to/working-with-multiple-command-hierarchies
- https://spectreconsole.net/cli/how-to/making-options-required
- https://spectreconsole.net/cli/reference/attribute-and-parameter-reference

## Install

```bash
dotnet add package Spectre.Console.Cli
```

## Multi-command parsing

```csharp
using Spectre.Console.Cli;

var app = new CommandApp();

app.Configure(config =>
{
    config.SetApplicationName("udpctl");
    config.SetApplicationVersion("1.0.0");

    config.Settings.CaseSensitivity = CaseSensitivity.None;
    config.Settings.StrictParsing = true;

    config.AddCommand<HealthCommand>("health")
        .WithDescription("Probe middleware health")
        .WithAlias("ping");

#if DEBUG
    config.PropagateExceptions();
    config.ValidateExamples();
#endif
});

return await app.RunAsync(args);
```

## Edge / hub subcommands

Use `AddBranch<TSettings>()` for grouped commands with shared options.

```csharp
using Spectre.Console.Cli;
using System.ComponentModel;

var app = new CommandApp();

app.Configure(config =>
{
    config.SetApplicationName("udpctl");

    config.AddBranch<NodeSettings>("edge", edge =>
    {
        edge.SetDescription("Manage edge nodes");

        edge.AddCommand<EdgeRunCommand>("run");
        edge.AddCommand<EdgeConnectCommand>("connect");
    });

    config.AddBranch<NodeSettings>("hub", hub =>
    {
        hub.SetDescription("Manage hub nodes");

        hub.AddCommand<HubRunCommand>("run");
        hub.AddCommand<HubPeerCommand>("peer");
    });
});

return await app.RunAsync(args);

public class NodeSettings : CommandSettings
{
    [CommandOption("-v|--verbose")]
    [Description("Verbose logging")]
    public bool Verbose { get; init; }

    [CommandOption("--bind <ENDPOINT>", isRequired: true)]
    [Description("Bind endpoint, e.g. 0.0.0.0:7000")]
    public required string Bind { get; init; }
}

public sealed class EdgeRunSettings : NodeSettings
{
    [CommandOption("--hub <ENDPOINT>", isRequired: true)]
    [Description("Hub endpoint")]
    public required string Hub { get; init; }
}

public sealed class EdgeConnectSettings : NodeSettings
{
    [CommandArgument(0, "<peer-id>")]
    public required string PeerId { get; init; }
}

public sealed class HubRunSettings : NodeSettings
{
    [CommandOption("--cluster-id <ID>", isRequired: true)]
    public required string ClusterId { get; init; }
}

public sealed class HubPeerSettings : NodeSettings
{
    [CommandArgument(0, "<peer-endpoint>")]
    public required string PeerEndpoint { get; init; }
}

public sealed class EdgeRunCommand : AsyncCommand<EdgeRunSettings>
{
    public override Task<int> ExecuteAsync(CommandContext context, EdgeRunSettings settings)
        => Task.FromResult(0);
}

public sealed class EdgeConnectCommand : Command<EdgeConnectSettings>
{
    public override int Execute(CommandContext context, EdgeConnectSettings settings) => 0;
}

public sealed class HubRunCommand : AsyncCommand<HubRunSettings>
{
    public override Task<int> ExecuteAsync(CommandContext context, HubRunSettings settings)
        => Task.FromResult(0);
}

public sealed class HubPeerCommand : Command<HubPeerSettings>
{
    public override int Execute(CommandContext context, HubPeerSettings settings) => 0;
}
```

Examples:

```bash
udpctl edge run --bind 0.0.0.0:7100 --hub 10.0.0.5:7000
udpctl hub run --bind 0.0.0.0:7000 --cluster-id prod-eu
udpctl edge connect node-17 --bind 0.0.0.0:7100
udpctl hub peer 10.0.0.9:7000 --bind 0.0.0.0:7000
```

## Option and cross-field validation

Use `isRequired: true` for single required options. Override `Validate()` for cross-option rules.

```csharp
using Spectre.Console.Cli;
using System.ComponentModel;

public sealed class SendSettings : CommandSettings
{
    [CommandOption("--payload <FILE>")]
    public string? PayloadFile { get; init; }

    [CommandOption("--text <TEXT>")]
    public string? Text { get; init; }

    [CommandOption("--mtu <BYTES>")]
    [DefaultValue(1200)]
    public int Mtu { get; init; } = 1200;

    [CommandOption("--retries <COUNT>")]
    [DefaultValue(3)]
    public int Retries { get; init; } = 3;

    public override ValidationResult Validate()
    {
        if (string.IsNullOrWhiteSpace(PayloadFile) == string.IsNullOrWhiteSpace(Text))
            return ValidationResult.Error("Specify exactly one of --payload or --text.");

        if (Mtu is < 576 or > 65507)
            return ValidationResult.Error("--mtu must be between 576 and 65507.");

        if (Retries < 0)
            return ValidationResult.Error("--retries must be >= 0.");

        return ValidationResult.Success();
    }
}
```

## Per-parameter validation attribute

Use `ParameterValidationAttribute` for reusable checks.

```csharp
using Spectre.Console.Cli;

public sealed class EndpointAttribute : ParameterValidationAttribute
{
    public override ValidationResult Validate(CommandParameterContext context)
    {
        if (context.Value is string s && s.Contains(':'))
            return ValidationResult.Success();

        return ValidationResult.Error("Expected host:port.");
    }
}

public sealed class ListenSettings : CommandSettings
{
    [CommandOption("--bind <ENDPOINT>", isRequired: true)]
    [Endpoint]
    public required string Bind { get; init; }
}
```

## Notes from current docs

- Commands/subcommands are registered in `CommandApp.Configure(...)`.
- Grouped subcommands use `AddBranch(...)` or `AddBranch<TSettings>(...)`.
- With `AddBranch<TSettings>`, every child settings type must inherit from the branch settings type.
- Arguments use `[CommandArgument(index, template)]`.
- Options use `[CommandOption(template, isRequired: ...)]`.
- `StrictParsing = false` makes unknown flags fall through as remaining arguments instead of errors.
- `ValidateExamples()` checks configured `WithExample(...)` entries at startup.

# System.CommandLine for a UDP Proxy

Latest checked: 2026-04-19.

- Stable package: `System.CommandLine` `2.0.6`
- Newer prerelease exists: `3.0.0-preview.3.26207.106`
- Official docs: <https://learn.microsoft.com/en-us/dotnet/standard/commandline/>
- API refs used: `Command.SetAction`, `ParseResult.GetValue`, `Option.AllowMultipleArgumentsPerToken`

## Install

```bash
dotnet add package System.CommandLine --version 2.0.6
```

## Minimal shape

```csharp
using System.CommandLine;

Option<string> listenHost = new("--listen-host") { DefaultValueFactory = _ => "0.0.0.0" };
Option<int> listenPort = new("--listen-port") { Required = true };
Option<string> targetHost = new("--target-host") { Required = true };
Option<int> targetPort = new("--target-port") { Required = true };
Option<bool> verbose = new("--verbose", "-v") { Recursive = true };

RootCommand root = new("UDP proxy");
root.Options.Add(verbose);

Command run = new("run", "Start proxy")
{
    listenHost, listenPort, targetHost, targetPort
};

root.Subcommands.Add(run);

run.SetAction(parseResult =>
{
    var config = new UdpProxyConfig(
        parseResult.GetValue(listenHost)!,
        parseResult.GetValue(listenPort),
        parseResult.GetValue(targetHost)!,
        parseResult.GetValue(targetPort),
        parseResult.GetValue(verbose));

    return RunProxy(config);
});

return root.Parse(args).Invoke();

record UdpProxyConfig(string ListenHost, int ListenPort, string TargetHost, int TargetPort, bool Verbose);
static int RunProxy(UdpProxyConfig config) => 0;
```

## Subcommands

```csharp
Command test = new("test", "Send one UDP packet through the proxy");
Argument<string> payload = new("payload");
test.Arguments.Add(payload);

test.SetAction(parseResult =>
{
    string text = parseResult.GetValue(payload)!;
    return SendTestPacket(text);
});

root.Subcommands.Add(test);

static int SendTestPacket(string payload) => 0;
```

## Options and arguments

```csharp
Option<string[]> allow = new("--allow")
{
    AllowMultipleArgumentsPerToken = true
};

Argument<string> mode = new("mode");

Command route = new("route") { allow, mode };
```

Valid forms:

```bash
udp-proxy route relay --allow 10.0.0.1 10.0.0.2
udp-proxy route relay --allow 10.0.0.1 --allow 10.0.0.2
```

## Binding pattern

Core docs use `ParseResult.GetValue(...)` inside `SetAction(...)`.

```csharp
run.SetAction(parseResult =>
{
    string host = parseResult.GetValue(listenHost)!;
    int port = parseResult.GetValue(listenPort);
    bool isVerbose = parseResult.GetValue(verbose);
    return 0;
});
```

By name is also supported:

```csharp
run.SetAction(parseResult =>
{
    int port = parseResult.GetValue<int>("--listen-port");
    return 0;
});
```

## Validation

```csharp
listenPort.Validators.Add(result =>
{
    if (result.GetValue(listenPort) is < 1 or > 65535)
    {
        result.AddError("--listen-port must be 1..65535");
    }
});
```

## Custom parse

```csharp
Option<IPEndPoint?> upstream = new("--upstream")
{
    CustomParser = result =>
    {
        if (result.Tokens.Count != 1)
        {
            result.AddError("--upstream requires host:port");
            return null;
        }

        var parts = result.Tokens[0].Value.Split(':', 2);
        if (parts.Length != 2 || !int.TryParse(parts[1], out var port))
        {
            result.AddError("Expected host:port");
            return null;
        }

        return new IPEndPoint(IPAddress.Parse(parts[0]), port);
    }
};
```

## Async invocation

```csharp
run.SetAction(async (parseResult, cancellationToken) =>
{
    var config = new UdpProxyConfig(
        parseResult.GetValue(listenHost)!,
        parseResult.GetValue(listenPort),
        parseResult.GetValue(targetHost)!,
        parseResult.GetValue(targetPort),
        parseResult.GetValue(verbose));

    await RunProxyAsync(config, cancellationToken);
    return 0;
});

return await root.Parse(args).InvokeAsync();

static Task RunProxyAsync(UdpProxyConfig config, CancellationToken cancellationToken) => Task.CompletedTask;
```

`InvokeAsync()` + `SetAction((parseResult, cancellationToken) => ...)` is the current doc pattern for long-running/network commands.

## CLI examples

```bash
udp-proxy run --listen-port 9000 --target-host 127.0.0.1 --target-port 9001
udp-proxy run --listen-host 0.0.0.0 --listen-port 5353 --target-host 10.0.0.20 --target-port 5353 -v
udp-proxy test "ping"
udp-proxy --help
udp-proxy --version
```

## Notes from current docs

- `RootCommand` automatically adds help/version behavior.
- Subcommands are added via `root.Subcommands.Add(...)`.
- Global options use `Recursive = true`.
- Help and parse errors are handled by `Parse(args).Invoke()` / `InvokeAsync()`.
- Official docs currently emphasize `SetAction` + `ParseResult.GetValue`, not old handler APIs.

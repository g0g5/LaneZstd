# Cocona CLI Notes

Latest published package/docs found: `Cocona` `2.2.0` on NuGet. Upstream repo is archived; README/release docs are the current reference.

## Install

```bash
dotnet add package Cocona --version 2.2.0
```

## Multi-command parsing

Supports both single-command and subcommand CLI shapes, with getopt-style short options like `-rf`.

```csharp
using Cocona;

var app = CoconaApp.Create();

app.AddCommand("send", (
    [Option('h')] string host,
    [Option('p')] int port,
    [Option('v')] bool verbose,
    [Argument] string payload) =>
{
    Console.WriteLine($"send {payload} -> {host}:{port}, verbose={verbose}");
});

app.AddCommand("listen", (
    [Option('p')] int port,
    [Option('r')] bool reusePort) =>
{
    Console.WriteLine($"listen {port}, reuse={reusePort}");
});

app.Run();
```

Examples:

```bash
udp send -h 127.0.0.1 -p 9000 "ping"
udp listen -pr 9000
```

## Validation

Uses `System.ComponentModel.DataAnnotations` attributes on options/arguments.

```csharp
using Cocona;
using System.ComponentModel.DataAnnotations;

var app = CoconaApp.Create();

app.AddCommand("serve", (
    [Range(1, 65535)] [Option('p')] int port,
    [MaxLength(64)] [Option('n')] string? nodeName,
    [Argument] string bindAddress) =>
{
    Console.WriteLine($"{bindAddress}:{port} ({nodeName})");
});

app.Run();
```

Custom validation:

```csharp
using System.ComponentModel.DataAnnotations;
using System.Net;

sealed class IpOrHostAttribute : ValidationAttribute
{
    protected override ValidationResult? IsValid(object? value, ValidationContext _)
        => value is string s &&
           (IPAddress.TryParse(s, out _) || Uri.CheckHostName(s) != UriHostNameType.Unknown)
            ? ValidationResult.Success
            : new ValidationResult("Expected IP or host name.");
}
```

```csharp
app.AddCommand("probe", ([IpOrHost] [Argument] string host) =>
{
    Console.WriteLine(host);
});
```

## Nested subcommands for `edge` / `hub`

Best fit for a UDP middleware CLI shaped like `app edge run` and `app hub run`.

```csharp
using Cocona;
using System.ComponentModel.DataAnnotations;

var app = CoconaApp.Create();

app.AddSubCommand("edge", edge =>
{
    edge.AddCommand("run", (
        [Option('b')] string bind,
        [Range(1, 65535)] [Option('p')] int port,
        [Option('u')] string? upstreamHost,
        [Range(1, 65535)] [Option('q')] int? upstreamPort) =>
    {
        Console.WriteLine($"edge {bind}:{port} -> {upstreamHost}:{upstreamPort}");
    });

    edge.AddCommand("stats", () => Console.WriteLine("edge stats"));
}).WithDescription("Edge node commands");

app.AddSubCommand("hub", hub =>
{
    hub.AddCommand("run", (
        [Option('b')] string bind,
        [Range(1, 65535)] [Option('p')] int port,
        [Option('f')] bool forward) =>
    {
        Console.WriteLine($"hub {bind}:{port}, forward={forward}");
    });

    hub.AddCommand("peers", () => Console.WriteLine("hub peers"));
}).WithDescription("Hub node commands");

app.Run();
```

Examples:

```bash
middleware edge run -b 0.0.0.0 -p 7001 -u hub.local -q 9000
middleware hub run -b 0.0.0.0 -p 9000 -f
middleware edge stats
middleware hub peers
```

## Reusable shared options

Use `ICommandParameterSet` when both `edge` and `hub` need common UDP settings.

```csharp
using Cocona;

public record UdpOptions(
    [Option('b')] string Bind,
    [Option('p')] int Port,
    [Option('v')] bool Verbose = false
) : ICommandParameterSet;
```

```csharp
app.AddCommand("edge-run", (UdpOptions udp, [Option('u')] string upstream) =>
{
    Console.WriteLine($"edge {udp.Bind}:{udp.Port} -> {upstream}, verbose={udp.Verbose}");
});
```

## Useful behaviors

- Non-null, non-bool parameters are required by default.
- Nullable parameters become optional.
- `bool` options act like flags.
- Arrays / `IEnumerable<T>` accept repeated options.
- Built-in help: `-h`, `--help`.
- Misspelled commands get suggestions.

## Sources

- `https://www.nuget.org/packages/Cocona`
- `https://github.com/mayuki/Cocona`
- `https://github.com/mayuki/Cocona/releases/tag/v2.2.0`

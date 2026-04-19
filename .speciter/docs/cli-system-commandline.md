# System.CommandLine for .NET 10 CLI

Current docs checked:

- Microsoft Learn overview: `https://learn.microsoft.com/en-us/dotnet/standard/commandline/`
- Microsoft Learn syntax: `https://learn.microsoft.com/en-us/dotnet/standard/commandline/syntax`
- API docs (`net-10.0-pp`): `System.CommandLine`, `Command`, `Option<T>`, `OptionValidation`
- GitHub repo release page shows latest release: `System.CommandLine v2.0.2` on `2026-01-13`

## Package

```xml
<ItemGroup>
  <PackageReference Include="System.CommandLine" Version="2.0.2" />
</ItemGroup>
```

## Multi-command CLI: `edge` and `hub`

```csharp
using System.CommandLine;

var verbose = new Option<bool>("--verbose", "-v")
{
    Description = "Verbose logging",
    Recursive = true
};

var listen = new Option<string>("--listen")
{
    Description = "UDP bind endpoint, e.g. 0.0.0.0:7000",
    Required = true
};

var hubEndpoint = new Option<string>("--hub")
{
    Description = "Hub endpoint, e.g. 10.0.0.10:7000",
    Required = true
};

var mode = new Option<string>("--mode")
{
    Description = "Transport mode"
};
mode.AcceptOnlyFromAmong("raw", "framed", "zstd");

var edge = new Command("edge", "Run edge UDP node");
edge.Add(listen);
edge.Add(hubEndpoint);
edge.Add(mode);
edge.SetAction(parse =>
{
    var endpoint = parse.GetValue(listen)!;
    var hub = parse.GetValue(hubEndpoint)!;
    var transport = parse.GetValue(mode) ?? "zstd";
    var isVerbose = parse.GetValue(verbose);

    Console.WriteLine($"edge listen={endpoint} hub={hub} mode={transport} verbose={isVerbose}");
});

var hub = new Command("hub", "Run hub UDP router");
hub.Add(listen);
hub.Add(mode);
hub.SetAction(parse =>
{
    var endpoint = parse.GetValue(listen)!;
    var transport = parse.GetValue(mode) ?? "zstd";
    var isVerbose = parse.GetValue(verbose);

    Console.WriteLine($"hub listen={endpoint} mode={transport} verbose={isVerbose}");
});

var root = new RootCommand("UDP middleware CLI");
root.Add(verbose);
root.Add(edge);
root.Add(hub);

return root.Parse(args).Invoke();
```

Example invocations:

```bash
lanez edge --listen 0.0.0.0:7001 --hub 10.0.0.10:7000 --mode zstd -v
lanez hub --listen 0.0.0.0:7000 --mode raw
```

## Option validation

Allowed values:

```csharp
var role = new Option<string>("--role");
role.AcceptOnlyFromAmong("edge", "hub");
```

Custom validation:

```csharp
var mtu = new Option<int>("--mtu")
{
    Description = "UDP payload limit"
};

mtu.Validators.Add(result =>
{
    if (result.Tokens.Count == 0)
    {
        return;
    }

    var value = result.GetValueOrDefault<int>();
    if (value is < 576 or > 65507)
    {
        result.AddError("--mtu must be between 576 and 65507.");
    }
});
```

Cross-option validation at command level:

```csharp
edge.Validators.Add(result =>
{
    var selectedMode = result.GetValue(mode);
    var hubValue = result.GetValue(hubEndpoint);

    if (selectedMode == "raw" && string.IsNullOrWhiteSpace(hubValue))
    {
        result.AddError("edge requires --hub when --mode raw is used.");
    }
});
```

Existing file/path validation:

```csharp
var config = new Option<FileInfo>("--config")
{
    Description = "Config file"
};
config.AcceptExistingOnly();
```

## Parse without invoking

Useful for tests:

```csharp
var parseResult = root.Parse("edge --listen 0.0.0.0:7001 --hub 10.0.0.10:7000");

if (parseResult.Errors.Count > 0)
{
    foreach (var error in parseResult.Errors)
    {
        Console.WriteLine(error.Message);
    }
}
```

## Notes from current docs

- Use `RootCommand` for the app entry point.
- Add subcommands via `root.Add(command)` or `root.Subcommands.Add(command)`.
- Use `Option<T>.Required = true` for required options.
- Use `Option.Recursive = true` for global options that flow to subcommands.
- Use `Option<T>.AcceptOnlyFromAmong(...)` for enum-like values.
- Use `Validators` on `Option` or `Command` for custom validation.
- `root.Parse(args).Invoke()` is the basic parse + execute path.

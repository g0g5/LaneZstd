## Project Overview
LaneZstd is a .NET 10 C# UDP tunneling and relay project with a CLI, shared runtime core, custom framing protocol, and Zstd compression.

## Structure Map
```text
LaneZstd/
|- LaneZstd.slnx                              # Solution entrypoint wiring source and test projects
|- src/                                       # Production code
|  |- LaneZstd.Cli/                           # CLI app for launching edge or hub modes
|  |  |- LaneZstd.Cli.csproj                  # Executable project definition and package references
|  |  \- Program.cs                           # CLI entrypoint, command graph, option validation, config mapping
|  |- LaneZstd.Core/                          # Runtime orchestration for UDP forwarding and session flow
|  |  |- LaneZstd.Core.csproj                 # Core library definition; references protocol and compression support
|  |  |- EdgeRuntime.cs                       # Edge-side tunnel runtime loop
|  |  |- HubRuntime.cs                        # Hub-side runtime and forwarding loop
|  |  |- HubSessionManager.cs                 # Tracks sessions and allocates per-edge resources
|  |  |- PayloadEncoder.cs                    # Applies framing-aware compression decisions
|  |  |- PayloadDecoder.cs                    # Reverses payload encoding and compression
|  |  |- UdpSocketIO.cs                       # Shared UDP socket send/receive helpers
|  |  |- RuntimeOptions.cs                    # Shared runtime tuning options
|  |  |- EdgeConfig.cs                        # Edge runtime configuration model
|  |  \- HubConfig.cs                         # Hub runtime configuration model
|  \- LaneZstd.Protocol/                      # Shared wire protocol primitives and codec
|     |- LaneZstd.Protocol.csproj             # Protocol library definition
|     |- LaneZstdFrameCodec.cs                # Reads, writes, and validates wire frames
|     |- ProtocolConstants.cs                 # Header layout and protocol constants
|     |- Frames.cs                            # Frame record models
|     |- LaneZstdFrameHeader.cs               # Parsed frame header model
|     |- FrameType.cs                         # Frame kind enum
|     \- SessionId.cs                         # Session identifier primitive
\- tests/                                     # Automated verification
   \- LaneZstd.Tests/                         # xUnit coverage for CLI, protocol, runtimes, and integration flows
      |- LaneZstd.Tests.csproj                # Test project referencing production projects
      |- CliTests.cs                          # CLI parsing and validation coverage
      |- ProtocolTests.cs                     # Frame codec and protocol correctness coverage
      |- EdgeRuntimeTests.cs                  # Edge runtime behavior tests
      |- HubRuntimeTests.cs                   # Hub runtime behavior tests
      \- MultiEdgeIntegrationTests.cs         # Multi-node integration scenarios
```

## Development Guide
- Build: `dotnet build LaneZstd.slnx`
- Test: `dotnet test LaneZstd.slnx`
- Publish: `dotnet publish src/LaneZstd.Cli/LaneZstd.Cli.csproj -c Release -p:PublishDir=./publish/`
- Benchmark: `dotnet run --project src/LaneZstd.Cli/LaneZstd.Cli.csproj -- bench`
- Benchmark example: `dotnet run --project src/LaneZstd.Cli/LaneZstd.Cli.csproj -- bench --duration-seconds 30 --warmup-seconds 3 --messages-per-second 200 --avg-payload-bytes 700 --min-payload-bytes 50 --max-payload-bytes 1350 --max-packet-size 1400 --output text`
- Typecheck: use `dotnet build LaneZstd.slnx`
- Verify changes: `dotnet build LaneZstd.slnx && dotnet test LaneZstd.slnx`

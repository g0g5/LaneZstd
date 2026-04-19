# LaneZstd

LaneZstd is a .NET 10 UDP tunneling and relay tool with two runtime modes, `edge` and `hub`. It uses a custom framing protocol over the tunnel and applies Zstd compression when payloads exceed a configured threshold.

## Usage

### Typical Topology

```text
UDP client <-> edge <==== framed UDP tunnel ====> hub <-> game server
```

- `edge` runs close to the client and listens for local UDP traffic.
- `hub` runs close to the server and accepts tunneled traffic from one or more `edge` nodes.
- `hub` allocates a dedicated local UDP port per edge session, then relays traffic to the actual game server.

### Requirements

- .NET 10 SDK
- Available UDP ports

### Build

```bash
dotnet build LaneZstd.slnx
```

### CLI Layout

```bash
dotnet run --project src/LaneZstd.Cli -- --help
dotnet run --project src/LaneZstd.Cli -- edge --help
dotnet run --project src/LaneZstd.Cli -- hub --help
```

Global option:

- `--verbose` / `-v`: Enables more detailed packet-level logging.

### Run `hub`

Minimal example:

```bash
dotnet run --project src/LaneZstd.Cli -- hub \
  --game 127.0.0.1:16261 \
  --session-port-range 40000-40031
```

Common options:

- `--bind`: Public hub bind endpoint, default `0.0.0.0:38441`
- `--game`: Local game server endpoint, required
- `--session-port-range`: Local UDP port range used for per-session sockets, required
- `--session-idle-timeout`: Session idle timeout in seconds, default `30`
- `--max-sessions`: Maximum concurrent sessions, default is the size of the port range
- `--compress-threshold`: Compression threshold in bytes, default `96`
- `--compression-level`: Zstd compression level, default `3`
- `--max-packet-size`: Maximum framed UDP packet size, default `1200`
- `--stats-interval`: Stats logging interval in seconds, default `5`; set to `0` to disable periodic stats

Validation rules:

- The `--bind` port must not overlap with `--session-port-range`.
- `--max-sessions` must not exceed the size of `--session-port-range`.

### Run `edge`

Minimal example:

```bash
dotnet run --project src/LaneZstd.Cli -- edge \
  --hub 203.0.113.10:38441 \
  --game-listen 127.0.0.1:38443
```

Common options:

- `--bind`: Local tunnel bind endpoint, default `0.0.0.0:38441`
- `--hub`: Hub endpoint, required
- `--game-listen`: Local game-facing UDP endpoint, required
- `--compress-threshold`: Compression threshold in bytes, default `96`
- `--compression-level`: Zstd compression level, default `3`
- `--max-packet-size`: Maximum framed UDP packet size, default `1200`
- `--stats-interval`: Stats logging interval in seconds, default `5`; set to `0` to disable periodic stats

Validation rules:

- `--bind` and `--game-listen` must be different endpoints.
- `--hub` and `--game-listen` cannot use a wildcard address such as `0.0.0.0`.

### End-to-End Example

Start a hub near the server:

```bash
dotnet run --project src/LaneZstd.Cli -- hub \
  --bind 0.0.0.0:38441 \
  --game 127.0.0.1:16261 \
  --session-port-range 40000-40015 \
  --stats-interval 5
```

Start an edge near the client:

```bash
dotnet run --project src/LaneZstd.Cli -- edge \
  --bind 0.0.0.0:38442 \
  --hub 203.0.113.10:38441 \
  --game-listen 127.0.0.1:38443 \
  --stats-interval 5
```

Then point your UDP client to `127.0.0.1:38443`. Traffic will flow through `edge -> hub -> game server`.

### Runtime Notes

- `edge` registers a session with `hub` when it starts.
- `edge` learns the first client that sends traffic to `--game-listen` and only forwards return traffic to that client.
- `hub` allocates a dedicated local session port for each edge session.
- Payloads larger than the compression threshold are compressed with Zstd when possible; smaller payloads stay raw to avoid unnecessary overhead.
- Idle sessions are cleaned up by `hub` after the configured timeout.

### Publish

Build distributable output:

```bash
dotnet publish src/LaneZstd.Cli/LaneZstd.Cli.csproj -c Release -p:PublishDir=./publish/
```

## Local Development

### Repository Layout

```text
src/
|- LaneZstd.Cli/       # CLI entrypoint and argument validation
|- LaneZstd.Core/      # Edge and hub runtimes, forwarding, compression and decompression
\- LaneZstd.Protocol/  # Custom wire protocol and frame codec

tests/
\- LaneZstd.Tests/     # xUnit test suite
```

### Common Commands

Build:

```bash
dotnet build LaneZstd.slnx
```

Test:

```bash
dotnet test LaneZstd.slnx
```

Full verification:

```bash
dotnet build LaneZstd.slnx && dotnet test LaneZstd.slnx
```

### Test Coverage

The test project lives in `tests/LaneZstd.Tests` and currently covers:

- CLI parsing and validation
- Protocol frame encoding and decoding
- Edge and hub runtime behavior
- Multi-edge integration scenarios

### Development Notes

- If you change CLI arguments, update both `src/LaneZstd.Cli/Program.cs` and `tests/LaneZstd.Tests/CliTests.cs`.
- If you change the protocol, extend `ProtocolTests` first.
- If you change forwarding or session behavior, run the integration tests that exercise loopback and multi-session flows.

### Local Debugging

For local debugging, open two terminals:

1. Run `hub` in one terminal.
2. Run `edge` in the other.

Add `--verbose` to either command when you need packet-level logs.

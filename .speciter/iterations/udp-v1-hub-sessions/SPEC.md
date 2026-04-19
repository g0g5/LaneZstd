# LaneZstd UDP v1 Hub Sessions

## Summary

- Iteration: `udp-v1-hub-sessions`
- Scope: `Spec + scaffold`
- Product target: multi-session, full-duplex, UDP-over-UDP Zstd compression middleware for game server hosting
- Runtime style: CLI flags only, console logging only, `C#/.NET 10`
- Verification baseline: unit tests plus local multi-edge loopback demo

## User-Confirmed Constraints

- Transport is UDP only. TCP is out of scope for v1.
- Full-duplex traffic is required.
- v1 uses an asymmetric runtime: many `edge` nodes connect to one `hub`.
- Hub accepts dynamic remote edges by default.
- Hub must preserve per-player identity toward the local game server by assigning one local UDP source port per session.
- Sessions use lightweight `Register` / `RegisterAck` control frames plus a `SessionId`.
- v1 explicitly targets `N edges -> 1 hub -> 1 local game server`.
- P2P or mesh networking is deferred.
- v1 prioritizes usability and clear behavior over maximum compression ratio.
- Default safe UDP payload ceiling is `1200B` after LaneZstd framing.
- v1 does not implement fragmentation or reassembly.

## Problem Statement

LaneZstd must sit explicitly between game clients and a game server. It is not a passive sniffer and does not transparently hijack existing UDP traffic. A client game sends UDP datagrams to a local `edge` endpoint. The edge forwards datagrams to a remote `hub` using a small self-framed protocol, optionally compressing each datagram with Zstandard. The hub fans many remote edges into one local game server while preserving distinct per-player identities by assigning each session its own local UDP port.

## Non-Goals

- No TCP transport or TCP fallback
- No fragmentation / reassembly
- No reliable delivery, ACK-based retransmission, or ordering guarantees
- No NAT traversal or peer discovery
- No P2P mesh or direct edge-to-edge routing
- No protocol-aware game packet parsing
- No GUI or config file in v1
- No transparent kernel-level interception
- No encryption or authentication in v1
- No NAT rebinding recovery; a session is pinned to the edge source endpoint that registered it

## External Dependencies

### Selected

- `System.CommandLine` `2.0.2`
  - Reason: first-party docs, direct subcommand support, recursive global options, and clean option / command validation for `edge` and `hub` command shapes
- `ZstdSharp.Port` `0.8.7`
  - Reason: maintained Zstd binding with `Span`-based `TryWrap` / `TryUnwrap` APIs that fit fixed-buffer UDP datagram processing well

### Evaluated Alternatives

- `Spectre.Console.Cli`
  - Viable for command trees, but more class-heavy than needed for a small runtime-first CLI
- `Cocona` `2.2.0`
  - Good ergonomics, but upstream is archived, so it should not be the primary choice for a new greenfield CLI
- `ZstdNet` `1.5.7`
  - Viable fallback, but `ZstdSharp.Port` remains the better default for caller-owned buffer paths

## Research Notes

### `System.CommandLine`

Install:

```xml
<ItemGroup>
  <PackageReference Include="System.CommandLine" Version="2.0.2" />
</ItemGroup>
```

Reference pattern:

```csharp
using System.CommandLine;

var verbose = new Option<bool>("--verbose", "-v") { Recursive = true };

var bind = new Option<string>("--bind") { Required = true };
var hub = new Option<string>("--hub") { Required = true };
var gameListen = new Option<string>("--game-listen") { Required = true };

var edge = new Command("edge", "Run edge node") { bind, hub, gameListen };

edge.Validators.Add(result =>
{
    var bindValue = result.GetValue(bind);
    var gameValue = result.GetValue(gameListen);

    if (bindValue == gameValue)
    {
        result.AddError("--bind and --game-listen must differ.");
    }
});

var root = new RootCommand("LaneZstd UDP hub runtime") { verbose, edge };
return await root.Parse(args).InvokeAsync();
```

### `ZstdSharp.Port`

Install:

```xml
<ItemGroup>
  <PackageReference Include="ZstdSharp.Port" Version="0.8.7" />
</ItemGroup>
```

Reference pattern:

```csharp
using ZstdSharp;

using var compressor = new Compressor(level: 3);
using var decompressor = new Decompressor();

if (!compressor.TryWrap(payload, compressedBuffer, out int compressedLength))
    throw new InvalidOperationException("UDP buffer too small");

if (!decompressor.TryUnwrap(compressed, payloadBuffer, out int payloadLength))
    throw new InvalidOperationException("Destination buffer too small");
```

## Runtime Model

v1 exposes two primary commands:

```bash
lanezstd edge --bind <local-tunnel-endpoint> --hub <remote-hub-endpoint> --game-listen <local-edge-endpoint>
lanezstd hub  --bind <public-hub-endpoint> --game <local-game-server-endpoint> --session-port-range <start-end>
```

Meaning:

- `edge`
  - Owns a local UDP endpoint for tunnel traffic on `--bind`
  - Owns a local UDP endpoint for the game client on `--game-listen`
  - Forwards client datagrams to one configured remote `hub`
  - Learns the local game client's source endpoint from the first datagram received on `--game-listen`
- `hub`
  - Owns one public UDP endpoint for tunnel traffic on `--bind`
  - Owns no single shared game-facing port for players
  - Creates one session per accepted remote edge registration
  - Allocates one local UDP port per session from `--session-port-range`
  - Uses that per-session socket to send to and receive from the local game server on `--game`

### Deployment Pattern

Client machine:

```bash
lanezstd edge \
  --bind 0.0.0.0:38441 \
  --hub 198.51.100.10:38441 \
  --game-listen 127.0.0.1:38443
```

Server machine:

```bash
lanezstd hub \
  --bind 0.0.0.0:38441 \
  --game 127.0.0.1:16261 \
  --session-port-range 40000-40999
```

Result:

- Each player sends to a local edge endpoint instead of directly to the server.
- The hub accepts many edges on one public UDP port.
- The local game server sees one distinct local source port per remote player session.
- LaneZstd still treats game datagrams as opaque payloads.

## Address Semantics

- `127.0.0.1`: loopback only; local processes can reach it, remote machines cannot
- `0.0.0.0`: bind-only wildcard; valid for listening, never valid as a send target
- v1 should accept explicit `ip:port` values only; hostname resolution is optional and not required for the first scaffold
- `--hub` must be a concrete remote endpoint, never a wildcard
- `--game-listen` should normally be loopback for client-side deployment
- `--game` on the hub should normally be loopback for local game server deployment

## Session Identity Model

- A hub session is identified by `SessionId` plus the registered remote edge endpoint.
- Each accepted session gets:
  - a generated `SessionId`
  - one remote edge UDP endpoint
  - one dedicated local UDP socket bound to an allocated port from `--session-port-range`
  - one target game server endpoint from `--game`
  - last activity timestamp and counters
- The dedicated per-session socket is the identity mechanism that lets the local game server distinguish players.
- The edge supports one active local game client endpoint in v1.
- The hub supports many concurrent sessions up to the configured port range and `--max-sessions` limit.

## Datagram Model

- One UDP datagram in maps to one LaneZstd frame out.
- One LaneZstd frame in maps to one UDP datagram out.
- No batching, splitting, stream packing, or cross-packet compression state.
- A bad packet affects only that packet or that session.
- Compression is decided independently per datagram.

## Compression Policy

Default v1 policy:

- `compress-threshold = 96B`
- `compression-level = 3`
- If payload length is below threshold: send raw `Data` frame
- If compressed result is not smaller than raw payload: send raw `Data` frame
- If framed output would exceed `max-packet-size` (default `1200`): drop packet and increment counters

Rationale:

- Small game packets often do not benefit from compression
- Raw passthrough keeps latency and implementation simpler
- Fixed max packet size avoids hidden IP fragmentation in v1

## Protocol

LaneZstd tunnel payload is one UDP datagram with a fixed-size header followed by an optional body.

### Header Layout

All integers are little-endian.

| Field | Size | Notes |
|---|---:|---|
| `Magic` | 2 bytes | constant `0x4C5A` (`LZ`) |
| `Version` | 1 byte | `1` for v1 |
| `FrameType` | 1 byte | values below |
| `Flags` | 1 byte | bit flags below |
| `Reserved` | 1 byte | must be `0` in v1 |
| `SessionId` | 4 bytes | `0` only for initial `Register` |
| `RawLength` | 2 bytes | original UDP payload length; `0` for empty control frames |
| `BodyLength` | 2 bytes | bytes stored after the header |

Header size: `14 bytes`

### Frame Types

- `0x01`: `Register`
- `0x02`: `RegisterAck`
- `0x03`: `Data`
- `0x04`: `Close`

### Flags

- `0x00`: raw body follows
- `0x01`: Zstd-compressed body follows

### Control Frame Bodies

- `Register`
  - body empty in v1
  - sent by edge on startup and again only after restart
- `RegisterAck`
  - body is `2 bytes` local allocated session port on the hub
  - sent by hub after session creation succeeds
- `Close`
  - body empty in v1
  - sent when a session is intentionally closed or timed out

### Validation Rules

- `Magic` mismatch: drop and count `protocol_error`
- `Version != 1`: drop and count `protocol_error`
- `Reserved != 0`: drop and count `protocol_error`
- `BodyLength` mismatch with datagram body: drop and count `protocol_error`
- `Register` must use `SessionId == 0`
- `RegisterAck`, `Data`, and `Close` must use `SessionId != 0`
- Raw `Data` frame where `BodyLength != RawLength`: drop and count `protocol_error`
- Compressed `Data` frame where `RawLength == 0`: drop and count `protocol_error`
- Decompressed bytes must equal `RawLength`, else drop and count `decompress_error`
- `Data` from an unknown session: drop and count `unknown_session`
- `Data` or `Close` from an endpoint that does not match the session owner: drop and count `session_sender_mismatch`

## Session Lifecycle

### Edge Startup

1. Bind tunnel socket on `--bind`.
2. Bind game-facing socket on `--game-listen`.
3. Send `Register` to configured `--hub`.
4. Wait for `RegisterAck`.
5. Cache assigned `SessionId` and move to active forwarding.

### Hub Registration

1. Receive `Register` on `--bind`.
2. If the remote endpoint already owns an active session, return the existing `RegisterAck`.
3. Else allocate a free port from `--session-port-range`.
4. Create a dedicated UDP socket bound to that port.
5. Create session state with generated `SessionId`.
6. Send `RegisterAck` back to the registering edge.

### Session Timeout

1. If no activity occurs for `--session-idle-timeout`, mark the session expired.
2. Attempt to send `Close` to the edge.
3. Dispose the dedicated game-facing socket.
4. Return the local port to the pool.

## Processing Flows

### Outbound: game client -> edge -> hub

1. Receive a plain UDP datagram on `edge --game-listen`.
2. Learn or confirm the local game client's source endpoint.
3. If no active session exists yet, buffer nothing; registration must complete first.
4. Apply compress-or-raw decision.
5. Build a `Data` frame with the assigned `SessionId`.
6. If framed packet size exceeds `max-packet-size`, drop and count `oversize_drop`.
7. Send framed datagram to configured `--hub`.

### Inbound: hub -> edge -> game client

1. Receive a tunnel datagram on `edge --bind`.
2. Accept datagrams only from configured `--hub`.
3. Parse and validate header.
4. If `RegisterAck`, update active session state.
5. If `Data`, decode raw or decompress as needed.
6. Send plain UDP datagram to the learned local game client endpoint.

### Outbound: edge -> hub -> game server

1. Receive a valid `Data` frame on `hub --bind`.
2. Resolve session by `SessionId` and remote endpoint.
3. Decode raw or decompress as needed.
4. Send the plain UDP payload to configured `--game` using the session's dedicated local socket.

### Inbound: game server -> hub -> edge

1. Receive a plain UDP datagram on one session's dedicated local socket.
2. Resolve the owning session by socket / local port.
3. Apply compress-or-raw decision.
4. Build a `Data` frame for that session.
5. If framed packet size exceeds `max-packet-size`, drop and count `oversize_drop`.
6. Send framed datagram to that session's remote edge endpoint.

## CLI Spec

v1 should expose two primary commands.

### Edge Command

```bash
lanezstd edge \
  --bind 0.0.0.0:38441 \
  --hub 203.0.113.25:38441 \
  --game-listen 127.0.0.1:38443 \
  --compress-threshold 96 \
  --compression-level 3 \
  --max-packet-size 1200 \
  --stats-interval 5
```

Required options:

- `--hub <ip:port>`
- `--game-listen <ip:port>`

Optional options:

- `--bind <ip:port>` default `0.0.0.0:38441`
- `--compress-threshold <bytes>` default `96`
- `--compression-level <n>` default `3`
- `--max-packet-size <bytes>` default `1200`
- `--stats-interval <seconds>` default `5`, `0` disables periodic stats
- `--verbose` enables packet-level debug logging

### Hub Command

```bash
lanezstd hub \
  --bind 0.0.0.0:38441 \
  --game 127.0.0.1:16261 \
  --session-port-range 40000-40999 \
  --session-idle-timeout 30 \
  --max-sessions 1000 \
  --compress-threshold 96 \
  --compression-level 3 \
  --max-packet-size 1200 \
  --stats-interval 5
```

Required options:

- `--game <ip:port>`
- `--session-port-range <start-end>`

Optional options:

- `--bind <ip:port>` default `0.0.0.0:38441`
- `--session-idle-timeout <seconds>` default `30`
- `--max-sessions <n>` default equals size of `session-port-range`
- `--compress-threshold <bytes>` default `96`
- `--compression-level <n>` default `3`
- `--max-packet-size <bytes>` default `1200`
- `--stats-interval <seconds>` default `5`, `0` disables periodic stats
- `--verbose` enables packet-level debug logging

Validation:

- Port range `1..65535`
- `bind`, `hub`, `game-listen`, and `game` send targets must not be wildcard `0.0.0.0`
- `max-packet-size >= 128`
- `compress-threshold >= 0`
- `compression-level` limited to the supported range chosen during implementation
- `session-idle-timeout >= 1`
- `max-sessions >= 1`
- `session-port-range` start must be `<=` end
- `session-port-range` must contain at least one port
- `--bind` must not overlap with any port in `--session-port-range`
- `edge --bind` and `edge --game-listen` must not be identical endpoints

## Observability

Counters required in v1:

- `sessions_created`
- `sessions_closed`
- `sessions_timed_out`
- `active_sessions`
- `edge_packets_in`
- `edge_packets_out`
- `hub_packets_in`
- `hub_packets_out`
- `game_packets_in`
- `game_packets_out`
- `raw_frames_out`
- `compressed_frames_out`
- `raw_bytes_in`
- `framed_bytes_out`
- `oversize_drop`
- `protocol_error`
- `decompress_error`
- `unknown_session`
- `session_sender_mismatch`
- `port_pool_exhausted`

Derived metrics printed in stats output:

- compression ratio = `framed_bytes_out / raw_bytes_in`
- compression savings = `1 - compression ratio`
- session utilization = `active_sessions / max_sessions`

Stats log example:

```text
sessions active=42 created=57 timeout=3 util=0.42 packets edge_in=1820 edge_out=1811 game_in=1809 game_out=1818 raw_out=1091 zstd_out=720 drop_oversize=4 proto_err=0 zstd_err=0 unknown_session=1 ratio=0.74
```

## Failure Handling

- Invalid frame: drop packet only
- Unknown remote sender for existing session: drop packet only
- `Register` when the session pool is exhausted: reject and count `port_pool_exhausted`
- Compression failure: fallback to raw frame if possible, else drop
- Decompression failure: drop packet only
- Dedicated session socket bind failure: fail that registration, keep hub alive
- Socket exception on send/receive: log and continue unless cancellation requested
- Application shutdown: stop receive loops, dispose sockets, close active sessions, flush final stats

## Project Scaffold

The first scaffold created after this spec should use this layout:

```text
src/
  LaneZstd.Cli/
  LaneZstd.Core/
  LaneZstd.Protocol/
tests/
  LaneZstd.Tests/
```

Responsibilities:

- `LaneZstd.Cli`
  - `System.CommandLine` entrypoint
  - option parsing, validation, config mapping
  - `edge` and `hub` command wiring
- `LaneZstd.Core`
  - UDP sockets, receive/send loops, counters, runtime orchestration
  - edge runtime
  - hub session manager and port pool
  - compression decision policy
- `LaneZstd.Protocol`
  - frame header constants
  - control/data frame encode/decode and validation
  - `SessionId` generation helpers and frame type definitions
- `LaneZstd.Tests`
  - protocol tests
  - compression policy tests
  - session manager tests
  - local multi-edge loopback integration test

## Implementation Notes

- Reuse one `Compressor` and one `Decompressor` per runtime worker; do not share across threads without synchronization.
- Use preallocated buffers sized from `max-packet-size` and `Compressor.GetCompressBound(...)`.
- Keep one tunnel receive loop on `edge --bind` and `hub --bind`.
- On the hub, use one dedicated UDP socket per active session to preserve a stable local source port toward the game server.
- Keep outbound and inbound paths isolated so a malformed tunnel packet cannot corrupt unrelated session state.
- Prefer explicit immutable config objects for `EdgeConfig` and `HubConfig`.

## Acceptance Criteria

1. A single `lanezstd hub` instance can relay full-duplex UDP datagrams for multiple simultaneous `lanezstd edge` instances to one local game server.
2. Each accepted session receives a distinct local source port from the configured port range.
3. The local game server can reply through those per-session ports and the hub routes each reply back to the correct edge.
4. Valid compressed `Data` frames round-trip back to the exact original datagram payload.
5. Raw fallback is used for small or non-beneficial datagrams.
6. Packets exceeding `max-packet-size` after framing are dropped and counted.
7. Unknown sessions and sender mismatches are dropped without affecting other sessions.
8. Periodic stats show packet counts, active session counts, and effective compression ratio.
9. Unit tests cover header encode/decode, validation failures, register / ack flows, session allocation / timeout behavior, compress/raw decision logic, and one multi-edge loopback path.

## Deferred to Later Iterations

- Fragmentation and reassembly
- P2P or mesh routing
- Remote allowlists or authn/authz
- Config file support
- Shared dictionaries
- Version negotiation beyond static v1
- Encryption/authentication
- NAT rebinding recovery
- Platform-specific service wrappers

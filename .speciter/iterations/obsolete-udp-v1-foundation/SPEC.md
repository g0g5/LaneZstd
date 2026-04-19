# LaneZstd UDP v1 Foundation

## Summary

- Iteration: `udp-v1-foundation`
- Scope: `Spec + scaffold`
- Product target: single-connection, full-duplex, UDP-over-UDP Zstd compression middleware for game networking
- Runtime style: CLI flags only, console logging only
- Verification baseline: unit tests plus local loopback demo

## User-Confirmed Constraints

- Transport is UDP only. TCP is out of scope for v1.
- Full-duplex traffic is required.
- One LaneZstd instance pair serves one logical game connection.
- Game ports may be adjusted to route traffic through LaneZstd explicitly.
- v1 prioritizes usability and clear behavior over maximum compression ratio.
- Default safe UDP payload ceiling is `1200B` after LaneZstd framing.
- v1 does not implement fragmentation or reassembly.
- CLI should use a 3-part address model. Standard names in this spec: `bind`, `peer`, `game`.

## Problem Statement

LaneZstd must sit explicitly between a local game process and a remote LaneZstd peer. It is not a passive sniffer and does not transparently hijack existing UDP traffic. The game must send to or receive from a LaneZstd-owned local UDP endpoint. LaneZstd then forwards datagrams to the remote LaneZstd peer over UDP using a minimal self-framed protocol, optionally compressing per datagram with Zstandard.

## Non-Goals

- No TCP transport or TCP fallback
- No fragmentation / reassembly
- No reliable delivery, ACK, retransmission, or ordering guarantees
- No NAT traversal or peer discovery
- No multi-client multiplexing
- No protocol-aware game packet parsing
- No GUI or config file in v1
- No transparent kernel-level interception

## External Dependencies

### Selected

- `System.CommandLine` `2.0.6`
  - Reason: current stable CLI library with first-party docs and clean subcommand/validation support
- `ZstdSharp.Port`
  - Reason: maintained Zstd binding with span-based APIs that fit per-datagram UDP processing well

### Evaluated Alternative

- `ZstdNet` `1.5.7`
  - Viable fallback, but v1 should prefer `ZstdSharp.Port` because the span-first API shape is simpler for fixed datagram buffers

## Research Notes

### `System.CommandLine`

Install:

```bash
dotnet add package System.CommandLine --version 2.0.6
```

Reference pattern:

```csharp
using System.CommandLine;

var bind = new Option<IPEndPoint?>("--bind");
var peer = new Option<IPEndPoint?>("--peer") { Required = true };
var game = new Option<IPEndPoint?>("--game") { Required = true };

var edge = new Command("edge") { bind, peer, game };

edge.SetAction(async (parseResult, cancellationToken) =>
{
    var config = new LaneZstdConfig(
        parseResult.GetValue(bind),
        parseResult.GetValue(peer)!,
        parseResult.GetValue(game)!);

    await RunAsync(config, cancellationToken);
    return 0;
});

return await new RootCommand { edge }.Parse(args).InvokeAsync();
```

### `ZstdSharp.Port`

Install:

```bash
dotnet add package ZstdSharp.Port
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

Each side runs the same symmetric command shape:

```bash
lanezstd edge --bind <local-lanezstd-endpoint> --peer <remote-lanezstd-endpoint> --game <local-game-endpoint>
```

Meaning:

- `bind`: local UDP endpoint owned by LaneZstd for tunnel traffic
- `peer`: remote LaneZstd UDP endpoint; incoming tunnel traffic must come from this endpoint
- `game`: local game UDP endpoint that LaneZstd forwards plain datagrams to and receives plain datagrams from

Example deployment:

Server machine:

```bash
lanezstd edge --bind 0.0.0.0:38441 --peer 203.0.113.25:38441 --game 127.0.0.1:16261
```

Client machine:

```bash
lanezstd edge --bind 0.0.0.0:38441 --peer 198.51.100.10:38441 --game 127.0.0.1:38443
```

Result:

- The game only communicates with a local LaneZstd-facing endpoint.
- The game sees LaneZstd as its local UDP peer.
- LaneZstd does not need to understand the game protocol. It only forwards opaque UDP datagrams.

## Address Semantics

- `127.0.0.1`: loopback only; local processes can reach it, remote machines cannot
- `0.0.0.0`: bind-only wildcard; means all local interfaces for listening; it is not a send target
- v1 should accept explicit IP:port values only; hostname resolution is optional and not required for the first scaffold

## Datagram Model

- One UDP datagram in maps to one LaneZstd frame out.
- One LaneZstd frame in maps to one UDP datagram out.
- No batching, splitting, stream packing, or cross-packet state.
- A bad packet affects only that packet.

## Compression Policy

Default v1 policy:

- `compress-threshold = 96B`
- `compression-level = 3`
- If payload length is below threshold: send raw frame
- If compressed result is not smaller than raw payload: send raw frame
- If framed output would exceed `max-packet-size` (default `1200`): drop packet and increment counters

Rationale:

- Small game packets often do not benefit from compression
- Raw passthrough keeps latency and implementation simpler
- Fixed max packet size avoids hidden IP fragmentation in v1

## Protocol

LaneZstd tunnel payload is a single UDP datagram with a fixed-size header followed by body.

### Header Layout

All integers are little-endian.

| Field | Size | Notes |
|---|---:|---|
| `Magic` | 2 bytes | constant `0x4C5A` (`LZ`) |
| `Version` | 1 byte | `1` for v1 |
| `Flags` | 1 byte | bit flags below |
| `RawLength` | 2 bytes | original UDP payload length |
| `BodyLength` | 2 bytes | bytes actually stored after header |

Header size: `8 bytes`

### Flags

- `0x00`: raw payload follows
- `0x01`: Zstd-compressed payload follows

### Validation Rules

- `Magic` mismatch: drop and count `protocol_error`
- `Version != 1`: drop and count `protocol_error`
- `BodyLength` mismatch with datagram body: drop and count `protocol_error`
- Raw frame where `BodyLength != RawLength`: drop and count `protocol_error`
- Compressed frame where `RawLength == 0`: drop and count `protocol_error`
- Decompressed bytes must equal `RawLength`, else drop and count `decompress_error`

## Processing Flows

### Outbound: game -> peer

1. Receive a UDP datagram from the local game-facing socket.
2. Reject packets from non-configured game endpoint if a strict endpoint was established.
3. If payload length `< compress-threshold`, emit raw frame.
4. Else try Zstd-compress into a preallocated buffer.
5. If compression fails or does not shrink payload, emit raw frame.
6. If framed packet size exceeds `max-packet-size`, drop and count `oversize_drop`.
7. Send framed datagram to configured `peer`.

### Inbound: peer -> game

1. Receive a LaneZstd datagram on `bind`.
2. Drop packets from any remote endpoint other than configured `peer`.
3. Parse and validate fixed header.
4. If frame is raw, copy body directly to game send buffer.
5. If frame is compressed, decompress into a preallocated payload buffer.
6. If final payload length exceeds game send limit, drop and count error.
7. Send plain UDP datagram to configured local `game` endpoint.

## CLI Spec

v1 should expose one primary command:

```bash
lanezstd edge \
  --bind 0.0.0.0:38441 \
  --peer 203.0.113.25:38441 \
  --game 127.0.0.1:16261 \
  --compress-threshold 96 \
  --compression-level 3 \
  --max-packet-size 1200 \
  --stats-interval 5
```

Required options:

- `--peer <ip:port>`
- `--game <ip:port>`

Optional options:

- `--bind <ip:port>` default `0.0.0.0:38441`
- `--compress-threshold <bytes>` default `96`
- `--compression-level <n>` default `3`
- `--max-packet-size <bytes>` default `1200`
- `--stats-interval <seconds>` default `5`, `0` disables periodic stats
- `--verbose` enables packet-level debug logging

Validation:

- Port range `1..65535`
- `max-packet-size >= 128`
- `compress-threshold >= 0`
- `compression-level` limited to the supported range chosen during implementation
- `peer` must not be wildcard `0.0.0.0`
- `game` should normally be loopback for the default deployment pattern

## Observability

Counters required in v1:

- `game_packets_in`
- `peer_packets_in`
- `peer_packets_out`
- `game_packets_out`
- `raw_frames_out`
- `compressed_frames_out`
- `raw_bytes_in`
- `framed_bytes_out`
- `oversize_drop`
- `protocol_error`
- `decompress_error`

Derived metrics printed in stats output:

- compression ratio = `framed_bytes_out / raw_bytes_in`
- compression savings = `1 - compression ratio`

Stats log example:

```text
packets game_in=120 peer_in=118 game_out=118 peer_out=120 raw_out=81 zstd_out=39 drop_oversize=2 proto_err=0 zstd_err=0 ratio=0.72
```

## Failure Handling

- Invalid frame: drop packet only
- Unknown remote sender: drop packet only
- Compression failure: fallback to raw frame if possible, else drop
- Decompression failure: drop packet only
- Socket exception on send/receive: log and continue unless cancellation requested
- Application shutdown: stop receive loops, dispose sockets, flush final stats

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
- `LaneZstd.Core`
  - UDP sockets, receive/send loops, counters, runtime orchestration
  - compression decision policy
- `LaneZstd.Protocol`
  - frame header constants
  - encode/decode and validation
- `LaneZstd.Tests`
  - protocol tests
  - compression policy tests
  - local loopback integration test for one edge pair

## Implementation Notes

- Reuse one `Compressor` and one `Decompressor` per runtime instance; do not share across threads without synchronization.
- Use preallocated buffers sized from `max-packet-size` and `Compressor.GetCompressBound(...)`.
- Prefer a single async receive loop per socket for v1.
- Keep outbound and inbound paths isolated so a malformed tunnel packet cannot corrupt game-side state.

## Acceptance Criteria

1. A single `lanezstd edge` instance pair can relay full-duplex UDP datagrams between two local test programs through loopback or LAN.
2. Valid compressed frames round-trip back to the exact original datagram payload.
3. Raw fallback is used for small or non-beneficial datagrams.
4. Packets exceeding `max-packet-size` after framing are dropped and counted.
5. Packets from an unexpected peer are dropped.
6. Periodic stats show packet counts and effective compression ratio.
7. Unit tests cover header encode/decode, validation failures, compress/raw decision logic, and one loopback demo path.

## Deferred to Later Iterations

- Fragmentation and reassembly
- Multiple concurrent game clients
- Config file support
- Shared dictionaries
- Peer handshake and version negotiation beyond static v1
- Encryption/authentication
- Platform-specific service wrappers

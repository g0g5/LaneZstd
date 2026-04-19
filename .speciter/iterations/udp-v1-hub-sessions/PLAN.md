# LaneZstd UDP v1 Hub Sessions Plan

## Phase 1: Scaffold the solution

1. Create the solution and project layout from the spec: `src/LaneZstd.Cli`, `src/LaneZstd.Core`, `src/LaneZstd.Protocol`, and `tests/LaneZstd.Tests`.
2. Add the required package references for `System.CommandLine` and `ZstdSharp.Port`.
3. Establish project references so the CLI depends on core and protocol code, and tests cover all runtime layers.
4. Add baseline build and test wiring so the scaffold compiles before runtime behavior is added.

Completed log:

- 2026-04-19 16:18:39 MSK: Created the `LaneZstd.slnx` solution and the Phase 1 project layout, added `System.CommandLine` and `ZstdSharp.Port`, wired project references for CLI and tests, replaced template placeholders with minimal scaffold code, and verified the baseline with `dotnet test LaneZstd.slnx`.

## Phase 2: Define protocol and shared contracts

1. Implement protocol constants, frame type definitions, flags, and header layout for LaneZstd v1.
2. Add `SessionId` generation and shared models for register, register-ack, data, and close frames.
3. Implement frame encode/decode helpers that operate on fixed buffers and preserve the one-datagram-in, one-frame-out model.
4. Enforce header and frame validation rules, including magic, version, reserved byte, body length, session rules, and compression-specific checks.
5. Add targeted protocol tests for valid frames, invalid headers, body mismatches, and decompress-length validation.

Completed log:

- 2026-04-19 16:22:05 MSK: Replaced the protocol placeholder with LaneZstd v1 constants, frame/session contracts, a fixed-buffer frame codec, validation helpers for header and data rules, a non-zero `SessionId` generator, and targeted protocol tests covering valid frames, invalid headers, body mismatches, session rules, and decompressed-length validation.

## Phase 3: Implement configuration and CLI validation

1. Define immutable config objects for `EdgeConfig`, `HubConfig`, and shared runtime settings.
2. Build the `edge` and `hub` commands with the required and optional options from the spec.
3. Add command validation for endpoint shapes, wildcard restrictions, packet-size limits, timeout limits, and session port range rules.
4. Map parsed CLI options into config objects and keep console logging / verbosity wiring isolated in the CLI layer.
5. Add tests for command validation and config mapping for the main success and failure cases.

Completed log:

- 2026-04-19 16:46:17 MSK: Implemented immutable edge and hub config mapping with shared runtime settings, refactored `--verbose` into a recursive global CLI-only option, added explicit CLI-layer defaults and validation for endpoint shapes, wildcard restrictions, packet size and timeout limits, bind/range overlap, and `max-sessions` rules, and extended the CLI test coverage for success paths plus main failure cases before verifying with `dotnet test LaneZstd.slnx`.

## Phase 4: Build shared runtime primitives

1. Implement endpoint parsing, port-range allocation, and reusable buffer sizing based on `max-packet-size` and compression bounds.
2. Add the compression policy that chooses raw vs. compressed output using the configured threshold, level, and packet ceiling.
3. Create counter and stats primitives for required metrics and derived values such as compression ratio and session utilization.
4. Add runtime-safe send/receive helpers that log socket errors and keep the process alive unless cancellation is requested.
5. Add unit tests for compression decisions, oversize handling, and port-pool behavior.

Completed log:

- 2026-04-19 16:50:03 MSK: Added shared core runtime primitives for phase 4, including reusable buffer sizing from the framed packet ceiling and Zstd compression bounds, a session port pool allocator, raw-vs-compressed payload encode/decode helpers with oversize-drop handling, thread-safe counters and derived stats snapshots, UDP socket send/receive wrappers that suppress cancellation and log socket errors, and unit tests covering buffer sizing, compression decisions, oversize handling, counter snapshots, and port-pool allocation before verifying with `dotnet test LaneZstd.slnx`.

## Phase 5: Implement the edge runtime

1. Bind the tunnel socket on `--bind` and the local game-facing socket on `--game-listen`.
2. Implement edge startup registration: send `Register`, wait for `RegisterAck`, cache the assigned `SessionId`, and reject tunnel traffic from unexpected senders.
3. Implement the outbound client-to-hub path: learn the local client endpoint, apply compression policy, frame packets, enforce `max-packet-size`, and forward to the configured hub.
4. Implement the inbound hub-to-client path: parse and validate frames, process `RegisterAck`, decode data frames, and forward payloads to the learned local client endpoint.
5. Handle `Close`, unknown states, and error counters without buffering pre-registration traffic or corrupting active session state.

Completed log:

- 2026-04-19 16:56:22 MSK: Implemented the Phase 5 edge runtime with bound tunnel and game sockets, startup `Register` / `RegisterAck` session activation, client-to-hub framing with compression and oversize drops, hub-to-client frame validation and decode forwarding, close and unknown-state handling, CLI execution wiring for `edge`, and loopback UDP tests covering registration, full-duplex relay, preregistration drops, unexpected tunnel senders, and session close behavior before verifying with `dotnet test LaneZstd.slnx`.

## Phase 6: Implement the hub runtime and session manager

1. Bind the public tunnel socket on `--bind` and create a session manager keyed by `SessionId` plus remote edge endpoint.
2. Implement registration handling: reuse existing sessions for the same edge endpoint or allocate a new port, socket, and `SessionId` when capacity allows.
3. Create one dedicated local UDP socket per accepted session so the game server sees a distinct local source port per player.
4. Implement the edge-to-game path: validate incoming tunnel frames, resolve the owning session, decode payloads, and forward them to `--game` through the session socket.
5. Implement the game-to-edge path: receive on each session socket, resolve the session by socket identity, apply compression policy, frame the reply, and send it back to the correct remote edge.
6. Add session idle timeout handling that emits `Close`, disposes the socket, releases the local port, updates counters, and keeps other sessions unaffected.
7. Count and isolate failure cases including unknown session, sender mismatch, port-pool exhaustion, registration bind failure, and decompression errors.

Completed log:

- 2026-04-19 17:02:30 MSK: Implemented the Phase 6 hub runtime with public tunnel handling, a session manager keyed by remote edge endpoint plus `SessionId`, dedicated per-session game-facing sockets and receive loops, edge-to-game decode forwarding, game-to-edge framing with compression policy and oversize drops, idle-timeout cleanup with `Close` emission and port release, CLI execution wiring for `hub`, socket shutdown handling for disposed session sockets, and hub runtime tests covering registration reuse, full-duplex relay, distinct local source ports, pool exhaustion, unknown sessions, sender mismatch rejection, and timeout cleanup before verifying with `dotnet test LaneZstd.slnx`.

## Phase 7: Add observability and shutdown behavior

1. Wire all required counters into the edge and hub paths for packets, frames, bytes, sessions, drops, and protocol errors.
2. Implement periodic stats logging with the required derived metrics and a clear single-line console format.
3. Ensure cancellation shuts down receive loops cleanly, disposes sockets, closes active sessions, and emits final stats.

Completed log:

- 2026-04-19 17:07:08 MSK: Completed Phase 7 by adding a shared runtime stats reporter with single-line periodic and final stats output, wiring periodic observability into both edge and hub runtimes, closing active sessions during shutdown with final counter updates and `Close` emission, updating shutdown expectations in runtime tests, adding coverage for stats formatting plus edge and hub final-stats behavior, and verifying the result with `dotnet test LaneZstd.slnx`.

## Phase 8: Verify the full v1 behavior

1. Add unit tests for register / ack flows, session allocation, timeout cleanup, sender mismatch rejection, and raw fallback behavior.
2. Build a local multi-edge loopback integration test that runs multiple edges against one hub and one local game server endpoint.
3. Verify full-duplex relay behavior, distinct per-session source ports, compressed round-trips, oversize drops, and session isolation.
4. Run the full test suite and confirm the scaffold meets the acceptance criteria defined in the spec.

Completed log:

- 2026-04-19 17:08:10 MSK: Completed Phase 8 by extending the targeted tests for non-beneficial compression raw fallback and oversize-drop behavior, adding a local multi-edge loopback integration test that runs two edges against one hub and one game socket while verifying full-duplex relay, distinct per-session source ports, raw and compressed traffic paths, and session isolation, and then verifying the full scaffold with `dotnet test LaneZstd.slnx`.

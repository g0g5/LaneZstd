# ZstdNet for UDP Middleware

Current upstream package: `ZstdNet 1.5.7` (NuGet updated `2026-02-17`; package is `netstandard2.0`, compatible with `net10.0`).

Install:

```bash
dotnet add package ZstdNet --version 1.5.7
```

Use for per-datagram UDP: `Compressor` / `Decompressor`.

Do not use for per-datagram UDP: `CompressionStream` / `DecompressionStream` unless you already want stream framing.

Reuse rules:

- `Compressor` / `Decompressor`: reusable, `IDisposable`, not thread-safe.
- `CompressionOptions` / `DecompressionOptions`: reusable, `IDisposable`, thread-safe.

## Minimal Datagram Compression

```csharp
using ZstdNet;

using var compressor = new Compressor(new CompressionOptions(compressionLevel: 3));

ReadOnlySpan<byte> payload = datagramPayload;
byte[] sendBuffer = new byte[Compressor.GetCompressBound(payload.Length)];

int compressedLength = compressor.Wrap(payload, sendBuffer, offset: 0);
ReadOnlySpan<byte> packet = sendBuffer.AsSpan(0, compressedLength);
```

## Minimal Datagram Decompression

```csharp
using ZstdNet;

using var decompressor = new Decompressor();

ReadOnlySpan<byte> packet = receivedPacket;
Span<byte> payloadBuffer = stackalloc byte[1500];

int payloadLength = decompressor.Unwrap(packet, payloadBuffer, bufferSizePrecheck: true);
ReadOnlySpan<byte> payload = payloadBuffer[..payloadLength];
```

## Reuse in a Worker

```csharp
using ZstdNet;

using var copts = new CompressionOptions(compressionLevel: 3);
using var dopts = new DecompressionOptions();
using var compressor = new Compressor(copts);
using var decompressor = new Decompressor(dopts);

int Compress(ReadOnlySpan<byte> src, Span<byte> dst) => compressor.Wrap(src, dst);
int Decompress(ReadOnlySpan<byte> src, Span<byte> dst) => decompressor.Unwrap(src, dst);
```

## With Dictionary

```csharp
using ZstdNet;

byte[] dict = LoadSharedDictionary();

using var copts = new CompressionOptions(dict, compressionLevel: 3);
using var dopts = new DecompressionOptions(dict);
using var compressor = new Compressor(copts);
using var decompressor = new Decompressor(dopts);

int compressedLength = compressor.Wrap(payload, sendBuffer, 0);
int payloadLength = decompressor.Unwrap(sendBuffer.AsSpan(0, compressedLength), payloadBuffer);
```

## Safe Untrusted Input

```csharp
using ZstdNet;

using var decompressor = new Decompressor();
byte[] payload = decompressor.Unwrap(receivedPacket, maxDecompressedSize: 64 * 1024);
```

## Helpful APIs

```csharp
int Compressor.GetCompressBound(int size)
ulong Decompressor.GetDecompressedSize(ReadOnlySpan<byte> src)

byte[] Compressor.Wrap(ReadOnlySpan<byte> src)
int Compressor.Wrap(ReadOnlySpan<byte> src, Span<byte> dst)

byte[] Decompressor.Unwrap(ReadOnlySpan<byte> src, int maxDecompressedSize = int.MaxValue)
int Decompressor.Unwrap(ReadOnlySpan<byte> src, Span<byte> dst, bool bufferSizePrecheck = true)
```

## Notes

- `Unwrap(byte[]/Span...)` throws `ZstdException` on malformed frames or too-small destination buffers.
- `Unwrap(byte[])` requires frame content size to be present; for fixed buffers, prefer `Unwrap(src, dst)`.
- For small UDP payload families, shared dictionaries are supported via `CompressionOptions(byte[] dict, ...)` and `DecompressionOptions(byte[] dict)`.
- Advanced parameters are available via `IReadOnlyDictionary<ZSTD_cParameter, int>` and `IReadOnlyDictionary<ZSTD_dParameter, int>`.

## Sources

- `https://www.nuget.org/packages/ZstdNet/1.5.7`
- `https://github.com/skbkontur/ZstdNet`
- `https://raw.githubusercontent.com/skbkontur/ZstdNet/master/ZstdNet/Compressor.cs`
- `https://raw.githubusercontent.com/skbkontur/ZstdNet/master/ZstdNet/Decompressor.cs`
- `https://raw.githubusercontent.com/skbkontur/ZstdNet/master/ZstdNet/CompressionOptions.cs`
- `https://raw.githubusercontent.com/skbkontur/ZstdNet/master/ZstdNet/DecompressionOptions.cs`
- `https://raw.githubusercontent.com/skbkontur/ZstdNet/master/ZstdNet.Tests/Binding_Tests.cs`

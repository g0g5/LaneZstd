# ZstdNet

Current package found: `ZstdNet 1.5.7` (`netstandard2.0`; compatible with modern .NET including `net8.0`/`net9.0` computed on NuGet).

Install:

```bash
dotnet add package ZstdNet --version 1.5.7
```

Best fit for per-datagram UDP: use `Compressor` / `Decompressor` on `byte[]` or `Span<byte>`. `CompressionStream` / `DecompressionStream` are for already-framed stream payloads, not individual UDP packets.

## Byte array

```csharp
using ZstdNet;

byte[] payload = GetUdpPayload();

using var compressor = new Compressor();
byte[] compressed = compressor.Wrap(payload);

using var decompressor = new Decompressor();
byte[] restored = decompressor.Unwrap(compressed, maxDecompressedSize: 64 * 1024);
```

## Preallocated datagram buffer

```csharp
using ZstdNet;

ReadOnlySpan<byte> payload = datagram;
byte[] compressedBuffer = new byte[Compressor.GetCompressBound(payload.Length)];

using var compressor = new Compressor(new CompressionOptions(compressionLevel: 3));
int compressedLength = compressor.Wrap(payload, compressedBuffer, 0);

ReadOnlySpan<byte> compressedDatagram = compressedBuffer.AsSpan(0, compressedLength);
Span<byte> restoredBuffer = stackalloc byte[1500];

using var decompressor = new Decompressor();
int restoredLength = decompressor.Unwrap(compressedDatagram, restoredBuffer, bufferSizePrecheck: true);
ReadOnlySpan<byte> restored = restoredBuffer[..restoredLength];
```

## With dictionary

```csharp
using ZstdNet;

byte[] dict = LoadSharedDictionary();
byte[] payload = GetUdpPayload();

using var copts = new CompressionOptions(dict, compressionLevel: 3);
using var dopts = new DecompressionOptions(dict);
using var compressor = new Compressor(copts);
using var decompressor = new Decompressor(dopts);

byte[] compressed = compressor.Wrap(payload);
byte[] restored = decompressor.Unwrap(compressed, maxDecompressedSize: 64 * 1024);
```

## Stream compression

```csharp
using ZstdNet;

await using var output = new MemoryStream();
await using (var zstd = new CompressionStream(output))
{
    await sourceStream.CopyToAsync(zstd);
}

byte[] compressed = output.ToArray();
```

## Stream decompression

```csharp
using ZstdNet;

await using var input = new MemoryStream(compressedBytes);
await using var zstd = new DecompressionStream(input);
await using var output = new MemoryStream();

await zstd.CopyToAsync(output);
byte[] restored = output.ToArray();
```

## Notes

- `Compressor` and `Decompressor` are not thread-safe; pool or keep one per worker/thread.
- `CompressionOptions` and `DecompressionOptions` are thread-safe; reuse them.
- `Unwrap(..., maxDecompressedSize: ...)` is the safe form for untrusted datagrams.
- `ZstdException` is thrown for malformed data or too-small destination buffers.

## Sources

- NuGet: `https://www.nuget.org/packages/ZstdNet/1.5.7`
- README: `https://github.com/skbkontur/ZstdNet`

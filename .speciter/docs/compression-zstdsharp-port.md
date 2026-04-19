# ZstdSharp.Port for UDP Datagrams

- Latest package seen: `ZstdSharp.Port` `0.8.7`.
- Upstream: `https://github.com/oleg-st/ZstdSharp`
- Base library version noted upstream: Zstandard `v1.5.7`.

## Fit for per-datagram UDP

- Prefer one Zstd frame per UDP datagram.
- For datagrams, prefer buffer APIs over stream wrappers: `Compressor.Wrap` / `TryWrap`, `Decompressor.Unwrap` / `TryUnwrap`.
- Reuse long-lived `Compressor` and `Decompressor` instances per middleware/worker; they keep reusable native-port contexts.
- Do not share a single instance concurrently across threads unless you add your own synchronization.

## Install

```xml
<PackageReference Include="ZstdSharp.Port" Version="0.8.7" />
```

## One-shot compress into a caller buffer

```csharp
using ZstdSharp;

var compressor = new Compressor(level: 3);

ReadOnlySpan<byte> payload = packet;
Span<byte> compressed = sendBuffer;

if (!compressor.TryWrap(payload, compressed, out var written))
    throw new InvalidOperationException("Destination too small.");

socket.Send(sendBuffer[..written]);
```

## One-shot decompress into a caller buffer

```csharp
using ZstdSharp;

var decompressor = new Decompressor();

ReadOnlySpan<byte> compressed = datagram;
Span<byte> payload = receiveBuffer;

if (!decompressor.TryUnwrap(compressed, payload, out var written))
    throw new InvalidOperationException("Destination too small.");

Process(payload[..written]);
```

## Reuse in middleware

```csharp
using ZstdSharp;

sealed class ZstdDatagramCodec : IDisposable
{
    private readonly Compressor _compressor = new(level: 3);
    private readonly Decompressor _decompressor = new();

    public bool TryCompress(ReadOnlySpan<byte> src, Span<byte> dst, out int written)
        => _compressor.TryWrap(src, dst, out written);

    public bool TryDecompress(ReadOnlySpan<byte> src, Span<byte> dst, out int written)
        => _decompressor.TryUnwrap(src, dst, out written);

    public void Dispose()
    {
        _compressor.Dispose();
        _decompressor.Dispose();
    }
}
```

## If you need exact output allocation

```csharp
using ZstdSharp;

int maxCompressed = Compressor.GetCompressBound(payload.Length);
byte[] rented = ArrayPool<byte>.Shared.Rent(maxCompressed);

try
{
    if (!compressor.TryWrap(payload, rented, out var written))
        throw new InvalidOperationException();

    socket.Send(rented.AsSpan(0, written));
}
finally
{
    ArrayPool<byte>.Shared.Return(rented);
}
```

## If the receiver knows original size

- Best UDP pattern: put original payload length in your datagram header, then call `TryUnwrap` into a pre-sized buffer.

```csharp
int originalLength = header.UncompressedLength;
byte[] rented = ArrayPool<byte>.Shared.Rent(originalLength);

try
{
    var dst = rented.AsSpan(0, originalLength);
    if (!decompressor.TryUnwrap(datagramPayload, dst, out var written) || written != originalLength)
        throw new InvalidDataException();

    Process(dst);
}
finally
{
    ArrayPool<byte>.Shared.Return(rented);
}
```

## Span-returning convenience APIs

```csharp
Span<byte> compressed = compressor.Wrap(payload);
Span<byte> plain = decompressor.Unwrap(compressed);
```

- These allocate a new backing array.
- For hot UDP paths, prefer `TryWrap` / `TryUnwrap` with pooled buffers.

## Stream-oriented APIs

- Available: `CompressionStream`, `DecompressionStream`.
- Better fit for stream transports or chunked pipelines than single UDP datagrams.
- Low-level chunked APIs also exist: `WrapStream`, `FlushStream`, `UnwrapStream`, `ResetStream`.

## Useful knobs

```csharp
compressor.SetParameter(ZstdSharp.Unsafe.ZSTD_cParameter.ZSTD_c_compressionLevel, 3);
compressor.SetParameter(ZstdSharp.Unsafe.ZSTD_cParameter.ZSTD_c_nbWorkers, Environment.ProcessorCount);
```

- `nbWorkers` is mainly relevant for stream/file-style compression, usually not tiny per-datagram payloads.

## Sources

- `https://www.nuget.org/packages/ZstdSharp.Port`
- `https://github.com/oleg-st/ZstdSharp`
- `https://raw.githubusercontent.com/oleg-st/ZstdSharp/master/src/ZstdSharp/Compressor.cs`
- `https://raw.githubusercontent.com/oleg-st/ZstdSharp/master/src/ZstdSharp/Decompressor.cs`

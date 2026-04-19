# ZstdSharp for UDP Datagrams

Sources:
- https://github.com/oleg-st/ZstdSharp
- https://raw.githubusercontent.com/oleg-st/ZstdSharp/master/src/ZstdSharp/Compressor.cs
- https://raw.githubusercontent.com/oleg-st/ZstdSharp/master/src/ZstdSharp/Decompressor.cs

Note: current upstream docs/repo use NuGet package `ZstdSharp.Port`. The older `ZstdSharp` NuGet package is stale.

## Install

```bash
dotnet add package ZstdSharp.Port
```

## Byte Array API

```csharp
using ZstdSharp;

byte[] payload = GetPayload();

using var compressor = new Compressor(level: 3);
byte[] compressed = compressor.Wrap(payload).ToArray();

using var decompressor = new Decompressor();
byte[] roundTrip = decompressor.Unwrap(compressed).ToArray();
```

## Span API for Per-Datagram UDP

```csharp
using ZstdSharp;

using var compressor = new Compressor(level: 3);

ReadOnlySpan<byte> payload = datagramPayload;
Span<byte> sendBuffer = udpSendBuffer;

if (!compressor.TryWrap(payload, sendBuffer, out int compressedLength))
    throw new InvalidOperationException("UDP buffer too small");

ReadOnlySpan<byte> compressedDatagram = sendBuffer[..compressedLength];
```

## Decompress into a Preallocated Span

```csharp
using ZstdSharp;

using var decompressor = new Decompressor();

ReadOnlySpan<byte> compressedDatagram = receivedDatagram;
Span<byte> payloadBuffer = udpReceiveScratch;

if (!decompressor.TryUnwrap(compressedDatagram, payloadBuffer, out int payloadLength))
    throw new InvalidOperationException("Destination buffer too small");

ReadOnlySpan<byte> payload = payloadBuffer[..payloadLength];
```

## Size Helpers

```csharp
int maxCompressed = Compressor.GetCompressBound(payloadLength);
ulong expectedDecompressed = Decompressor.GetDecompressedSize(compressedDatagram);
```

## Minimal Datagram Pattern

```csharp
using ZstdSharp;

static int CompressDatagram(ReadOnlySpan<byte> src, Span<byte> dst, Compressor compressor)
{
    if (!compressor.TryWrap(src, dst, out int written))
        throw new InvalidOperationException("Destination datagram too small");
    return written;
}

static int DecompressDatagram(ReadOnlySpan<byte> src, Span<byte> dst, Decompressor decompressor)
{
    if (!decompressor.TryUnwrap(src, dst, out int written))
        throw new InvalidOperationException("Destination payload buffer too small");
    return written;
}
```

## Relevant Safe APIs

```csharp
Span<byte> Compressor.Wrap(ReadOnlySpan<byte> src)
int Compressor.Wrap(ReadOnlySpan<byte> src, Span<byte> dest)
bool Compressor.TryWrap(ReadOnlySpan<byte> src, Span<byte> dest, out int written)

Span<byte> Decompressor.Unwrap(ReadOnlySpan<byte> src, int maxDecompressedSize = int.MaxValue)
int Decompressor.Unwrap(ReadOnlySpan<byte> src, Span<byte> dest)
bool Decompressor.TryUnwrap(ReadOnlySpan<byte> src, Span<byte> dest, out int written)
```

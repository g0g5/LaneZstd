using LaneZstd.Protocol;
using ZstdSharp;

namespace LaneZstd.Core;

public sealed class PayloadEncoder : IDisposable
{
    private readonly RuntimeOptions _runtimeOptions;
    private readonly Compressor _compressor;

    public PayloadEncoder(RuntimeOptions runtimeOptions)
        : this(runtimeOptions, RuntimeBufferSizing.Create(runtimeOptions.MaxPacketSize))
    {
    }

    public PayloadEncoder(RuntimeOptions runtimeOptions, RuntimeBufferSizing bufferSizing)
    {
        _runtimeOptions = runtimeOptions;
        BufferSizing = bufferSizing;
        _compressor = new Compressor(level: runtimeOptions.CompressionLevel);
    }

    public RuntimeBufferSizing BufferSizing { get; }

    public PayloadEncodeResult Encode(ReadOnlySpan<byte> payload, Span<byte> destination, out int bytesWritten)
    {
        bytesWritten = 0;

        if (payload.Length > ushort.MaxValue)
        {
            return new PayloadEncodeResult(PayloadEncodingKind.DroppedOversize, payload.Length, 0, 0);
        }

        if (destination.Length < BufferSizing.MaxCompressedPayloadSize)
        {
            throw new ArgumentException("Destination buffer is smaller than the configured compression bound.", nameof(destination));
        }

        var rawFramedLength = ProtocolConstants.HeaderSize + payload.Length;
        var rawFits = rawFramedLength <= _runtimeOptions.MaxPacketSize;

        if (payload.Length < _runtimeOptions.CompressThreshold)
        {
            return WriteRaw(payload, rawFits, destination, out bytesWritten);
        }

        if (_compressor.TryWrap(payload, destination, out var compressedLength) &&
            compressedLength < payload.Length)
        {
            var compressedFramedLength = ProtocolConstants.HeaderSize + compressedLength;
            if (compressedFramedLength <= _runtimeOptions.MaxPacketSize)
            {
                bytesWritten = compressedLength;
                return new PayloadEncodeResult(PayloadEncodingKind.Compressed, payload.Length, compressedLength, compressedFramedLength);
            }
        }

        return WriteRaw(payload, rawFits, destination, out bytesWritten);
    }

    public void Dispose() => _compressor.Dispose();

    private static PayloadEncodeResult WriteRaw(ReadOnlySpan<byte> payload, bool rawFits, Span<byte> destination, out int bytesWritten)
    {
        bytesWritten = 0;
        if (!rawFits)
        {
            return new PayloadEncodeResult(PayloadEncodingKind.DroppedOversize, payload.Length, 0, 0);
        }

        payload.CopyTo(destination);
        bytesWritten = payload.Length;
        return new PayloadEncodeResult(PayloadEncodingKind.Raw, payload.Length, payload.Length, ProtocolConstants.HeaderSize + payload.Length);
    }
}

using LaneZstd.Protocol;
using ZstdSharp;

namespace LaneZstd.Core;

public sealed class PayloadDecoder : IDisposable
{
    private readonly Decompressor _decompressor = new();

    public bool TryDecode(LaneZstdFrameHeader header, ReadOnlySpan<byte> body, Span<byte> destination, out int bytesWritten)
    {
        bytesWritten = 0;

        if (!header.IsCompressed)
        {
            if (destination.Length < body.Length)
            {
                return false;
            }

            body.CopyTo(destination);
            bytesWritten = body.Length;
            return true;
        }

        if (!_decompressor.TryUnwrap(body, destination, out bytesWritten))
        {
            return false;
        }

        return LaneZstdFrameCodec.ValidateDecodedDataLength(header, bytesWritten) == FrameValidationError.None;
    }

    public void Dispose() => _decompressor.Dispose();
}

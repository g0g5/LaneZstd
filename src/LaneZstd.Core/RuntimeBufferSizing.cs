using LaneZstd.Protocol;
using ZstdSharp;

namespace LaneZstd.Core;

public sealed record RuntimeBufferSizing(
    int MaxPacketSize,
    int MaxPayloadSize,
    int MaxCompressedPayloadSize,
    int MaxReceiveBufferSize)
{
    public static RuntimeBufferSizing Create(int maxPacketSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPacketSize, ProtocolConstants.HeaderSize + 1);

        var maxPayloadSize = maxPacketSize - ProtocolConstants.HeaderSize;
        var maxCompressedPayloadSize = checked((int)Compressor.GetCompressBound(maxPayloadSize));
        var maxReceiveBufferSize = Math.Max(maxPacketSize, maxCompressedPayloadSize);

        return new RuntimeBufferSizing(maxPacketSize, maxPayloadSize, maxCompressedPayloadSize, maxReceiveBufferSize);
    }
}

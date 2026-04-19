using LaneZstd.Protocol;
using ZstdSharp;

namespace LaneZstd.Core;

public sealed record RuntimeBufferSizing(
    int MaxPacketSize,
    int MaxTunnelPayloadSize,
    int MaxDatagramPayloadSize,
    int MaxCompressedDatagramSize,
    int MaxTunnelReceiveBufferSize)
{
    private const int MaxProtocolPayloadSize = ushort.MaxValue;

    public static RuntimeBufferSizing Create(int maxPacketSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPacketSize, ProtocolConstants.HeaderSize + 1);

        var maxTunnelPayloadSize = maxPacketSize - ProtocolConstants.HeaderSize;
        var maxCompressedDatagramSize = checked((int)Compressor.GetCompressBound(MaxProtocolPayloadSize));

        return new RuntimeBufferSizing(
            maxPacketSize,
            maxTunnelPayloadSize,
            MaxProtocolPayloadSize,
            maxCompressedDatagramSize,
            maxPacketSize);
    }
}

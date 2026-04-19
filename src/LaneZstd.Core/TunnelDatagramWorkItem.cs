using LaneZstd.Protocol;

namespace LaneZstd.Core;

public sealed class TunnelDatagramWorkItem : IDisposable
{
    public TunnelDatagramWorkItem(PooledDatagram datagram, LaneZstdFrameHeader header)
    {
        Datagram = datagram;
        Header = header;
    }

    public PooledDatagram Datagram { get; }

    public LaneZstdFrameHeader Header { get; }

    public ReadOnlyMemory<byte> BodyMemory => Datagram.Memory.Slice(ProtocolConstants.HeaderSize, Header.BodyLength);

    public ReadOnlySpan<byte> Body => Datagram.Span.Slice(ProtocolConstants.HeaderSize, Header.BodyLength);

    public void Dispose() => Datagram.Dispose();
}

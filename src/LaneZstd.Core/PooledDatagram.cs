using System.Buffers;
using System.Net;
using System.Threading;

namespace LaneZstd.Core;

public sealed class PooledDatagram : IDisposable
{
    private byte[]? _buffer;

    public PooledDatagram(byte[] buffer, int length, IPEndPoint remoteEndPoint)
    {
        _buffer = buffer;
        Length = length;
        RemoteEndPoint = remoteEndPoint;
    }

    public int Length { get; }

    public IPEndPoint RemoteEndPoint { get; }

    public ReadOnlyMemory<byte> Memory => _buffer is null ? ReadOnlyMemory<byte>.Empty : _buffer.AsMemory(0, Length);

    public ReadOnlySpan<byte> Span => _buffer is null ? ReadOnlySpan<byte>.Empty : _buffer.AsSpan(0, Length);

    public void Dispose()
    {
        var buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace LaneZstd.Core;

public static class UdpSocketIO
{
    private const int TargetSocketBufferBytes = 1 << 20;

    public static void ConfigureBuffers(Socket socket)
    {
        ArgumentNullException.ThrowIfNull(socket);

        socket.ReceiveBufferSize = TargetSocketBufferBytes;
        socket.SendBufferSize = TargetSocketBufferBytes;
    }

    public static async ValueTask<bool> TrySendAsync(
        Socket socket,
        ReadOnlyMemory<byte> buffer,
        EndPoint remoteEndPoint,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        try
        {
            _ = await socket.SendToAsync(buffer, SocketFlags.None, remoteEndPoint, cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (SocketException exception) when (cancellationToken.IsCancellationRequested || exception.SocketErrorCode is SocketError.OperationAborted or SocketError.Interrupted)
        {
            return false;
        }
        catch (SocketException exception) when (!cancellationToken.IsCancellationRequested)
        {
            log?.Invoke($"socket send failed: {exception.SocketErrorCode} {exception.Message}");
            return false;
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    public static async ValueTask<UdpSocketReceiveResult?> TryReceiveFromAsync(
        Socket socket,
        Memory<byte> buffer,
        EndPoint remoteEndPoint,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, remoteEndPoint, cancellationToken);
            return new UdpSocketReceiveResult(result.ReceivedBytes, result.RemoteEndPoint);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (SocketException exception) when (cancellationToken.IsCancellationRequested || exception.SocketErrorCode is SocketError.OperationAborted or SocketError.Interrupted)
        {
            return null;
        }
        catch (SocketException exception) when (!cancellationToken.IsCancellationRequested)
        {
            log?.Invoke($"socket receive failed: {exception.SocketErrorCode} {exception.Message}");
            return null;
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
    }

    public static Channel<PooledDatagram> CreateDatagramChannel(int capacity, bool singleReader)
    {
        return Channel.CreateBounded<PooledDatagram>(new BoundedChannelOptions(capacity)
        {
            SingleWriter = true,
            SingleReader = singleReader,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    public static async Task RunReceivePumpAsync(
        Socket socket,
        int receiveBufferSize,
        EndPoint remoteEndPoint,
        ChannelWriter<PooledDatagram> writer,
        RuntimeCounters counters,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                byte[]? rentedBuffer = null;

                try
                {
                    rentedBuffer = ArrayPool<byte>.Shared.Rent(receiveBufferSize);
                    var result = await socket.ReceiveFromAsync(rentedBuffer.AsMemory(0, receiveBufferSize), SocketFlags.None, remoteEndPoint, cancellationToken);

                    if (result.RemoteEndPoint is not IPEndPoint receivedRemoteEndPoint)
                    {
                        continue;
                    }

                    var datagram = new PooledDatagram(
                        rentedBuffer,
                        result.ReceivedBytes,
                        new IPEndPoint(receivedRemoteEndPoint.Address, receivedRemoteEndPoint.Port));
                    rentedBuffer = null;

                    if (writer.TryWrite(datagram))
                    {
                        counters.Increment(RuntimeCounter.QueueEnqueued);
                        continue;
                    }

                    datagram.Dispose();
                    counters.Increment(RuntimeCounter.QueueDropped);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (SocketException exception) when (cancellationToken.IsCancellationRequested || exception.SocketErrorCode is SocketError.OperationAborted or SocketError.Interrupted)
                {
                    break;
                }
                catch (SocketException exception) when (!cancellationToken.IsCancellationRequested)
                {
                    log?.Invoke($"socket receive failed: {exception.SocketErrorCode} {exception.Message}");
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                finally
                {
                    if (rentedBuffer is not null)
                    {
                        ArrayPool<byte>.Shared.Return(rentedBuffer);
                    }
                }
            }
        }
        finally
        {
            writer.TryComplete();
            counters.Increment(RuntimeCounter.QueueCompleted);
        }
    }
}

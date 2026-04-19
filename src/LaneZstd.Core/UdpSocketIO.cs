using System.Net;
using System.Net.Sockets;

namespace LaneZstd.Core;

public static class UdpSocketIO
{
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
}

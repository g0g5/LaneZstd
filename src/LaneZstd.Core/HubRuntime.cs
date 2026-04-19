using System.Net;
using System.Net.Sockets;

using LaneZstd.Protocol;

namespace LaneZstd.Core;

public sealed class HubRuntime
{
    private readonly HubConfig _config;
    private readonly RuntimeCounters _counters;
    private readonly Action<string>? _log;
    private readonly bool _verbose;
    private readonly List<Task> _sessionTasks = [];
    private readonly object _sessionTaskSync = new();

    public HubRuntime(HubConfig config, RuntimeCounters? counters = null, Action<string>? log = null, bool verbose = false)
    {
        _config = config;
        _counters = counters ?? new RuntimeCounters();
        _log = log;
        _verbose = verbose;
    }

    public RuntimeCounters Counters => _counters;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var tunnelSocket = CreateBoundSocket(_config.BindEndpoint);
        var gameEndPoint = ToIPEndPoint(_config.GameEndpoint);
        var bufferSizing = RuntimeBufferSizing.Create(_config.Runtime.MaxPacketSize);
        var sessionManager = new HubSessionManager(_config.SessionPortRange, _config.MaxSessions);
        using var decoder = new PayloadDecoder();

        _log?.Invoke($"hub tunnel listening on {_config.BindEndpoint}; game target {_config.GameEndpoint}; session ports {_config.SessionPortRange}");

        try
        {
            await Task.WhenAll(
                RuntimeStatsReporter.RunPeriodicAsync("hub", _counters, _config.MaxSessions, _config.Runtime.StatsIntervalSeconds, _log, cancellationToken),
                RunTunnelLoopAsync(tunnelSocket, gameEndPoint, bufferSizing, decoder, sessionManager, cancellationToken),
                RunTimeoutLoopAsync(tunnelSocket, sessionManager, cancellationToken));
        }
        finally
        {
            await CloseSessionsAsync(tunnelSocket, sessionManager.RemoveAllSessions(), timedOut: false, countClosed: true, CancellationToken.None);
            await WaitForSessionTasksAsync();
            RuntimeStatsReporter.LogFinal("hub", _counters, _config.MaxSessions, _log);
        }
    }

    private async Task RunTunnelLoopAsync(
        Socket tunnelSocket,
        IPEndPoint gameEndPoint,
        RuntimeBufferSizing bufferSizing,
        PayloadDecoder decoder,
        HubSessionManager sessionManager,
        CancellationToken cancellationToken)
    {
        var receiveBuffer = new byte[bufferSizing.MaxReceiveBufferSize];
        var decodeBuffer = new byte[bufferSizing.MaxPayloadSize];

        while (!cancellationToken.IsCancellationRequested)
        {
            var receiveResult = await UdpSocketIO.TryReceiveFromAsync(
                tunnelSocket,
                receiveBuffer,
                CreateReceiveEndPoint(_config.BindEndpoint.Address),
                _log,
                cancellationToken);

            if (receiveResult is not UdpSocketReceiveResult received)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                continue;
            }

            _counters.Increment(RuntimeCounter.HubPacketsIn);

            if (received.RemoteEndPoint is not IPEndPoint remoteEdgeEndPoint)
            {
                continue;
            }

            if (!LaneZstdFrameCodec.TryRead(receiveBuffer.AsSpan(0, received.ReceivedBytes), out var header, out var body, out _))
            {
                _counters.Increment(RuntimeCounter.ProtocolError);
                continue;
            }

            switch (header.FrameType)
            {
                case FrameType.Register:
                    await HandleRegisterAsync(tunnelSocket, remoteEdgeEndPoint, gameEndPoint, bufferSizing, sessionManager, cancellationToken);
                    break;
                case FrameType.Data:
                    await HandleDataAsync(tunnelSocket, remoteEdgeEndPoint, gameEndPoint, header, body.ToArray(), decodeBuffer, decoder, sessionManager, cancellationToken);
                    break;
                case FrameType.Close:
                    HandleClose(remoteEdgeEndPoint, header, sessionManager);
                    break;
                default:
                    _counters.Increment(RuntimeCounter.ProtocolError);
                    break;
            }
        }
    }

    private async Task HandleRegisterAsync(
        Socket tunnelSocket,
        IPEndPoint remoteEdgeEndPoint,
        IPEndPoint gameEndPoint,
        RuntimeBufferSizing bufferSizing,
        HubSessionManager sessionManager,
        CancellationToken cancellationToken)
    {
        HubSession? session;
        var created = false;

        try
        {
            if (!sessionManager.TryGetOrCreateSession(
                    remoteEdgeEndPoint,
                    allocatedPort => CreateBoundSocket(new UdpEndpoint(gameEndPoint.Address, allocatedPort)),
                    out session,
                    out created,
                    out var isExhausted))
            {
                if (isExhausted)
                {
                    _counters.Increment(RuntimeCounter.PortPoolExhausted);
                }

                return;
            }
        }
        catch (SocketException exception)
        {
            _log?.Invoke($"hub failed to bind session socket for {remoteEdgeEndPoint}: {exception.SocketErrorCode} {exception.Message}");
            return;
        }

        if (session is null)
        {
            return;
        }

        session.Touch();

        if (created)
        {
            _counters.Increment(RuntimeCounter.SessionsCreated);
            _counters.ChangeActiveSessions(1);
            StartSessionLoop(session, tunnelSocket, gameEndPoint, bufferSizing, cancellationToken);

            if (_verbose)
            {
                _log?.Invoke($"hub created session {session.SessionId} for {remoteEdgeEndPoint} on local port {session.AllocatedPort}");
            }
        }

        Span<byte> ackBuffer = stackalloc byte[ProtocolConstants.HeaderSize + sizeof(ushort)];
        if (!LaneZstdFrameCodec.TryWriteRegisterAck(new RegisterAckFrame(session.SessionId, (ushort)session.AllocatedPort), ackBuffer, out var ackBytesWritten))
        {
            _counters.Increment(RuntimeCounter.ProtocolError);
            return;
        }

        if (await UdpSocketIO.TrySendAsync(tunnelSocket, ackBuffer[..ackBytesWritten].ToArray(), remoteEdgeEndPoint, _log, cancellationToken))
        {
            _counters.Increment(RuntimeCounter.HubPacketsOut);
        }
    }

    private async Task HandleDataAsync(
        Socket tunnelSocket,
        IPEndPoint remoteEdgeEndPoint,
        IPEndPoint gameEndPoint,
        LaneZstdFrameHeader header,
        ReadOnlyMemory<byte> body,
        byte[] decodeBuffer,
        PayloadDecoder decoder,
        HubSessionManager sessionManager,
        CancellationToken cancellationToken)
    {
        if (!sessionManager.TryGetOwnedSession(header.SessionId, remoteEdgeEndPoint, out var session, out var isSenderMismatch))
        {
            _counters.Increment(isSenderMismatch ? RuntimeCounter.SessionSenderMismatch : RuntimeCounter.UnknownSession);
            return;
        }

        if (session is null)
        {
            return;
        }

        if (!decoder.TryDecode(header, body.Span, decodeBuffer, out var decodedBytesWritten))
        {
            _counters.Increment(RuntimeCounter.DecompressError);
            return;
        }

        session.Touch();

        if (!await UdpSocketIO.TrySendAsync(session.GameSocket, decodeBuffer.AsMemory(0, decodedBytesWritten), gameEndPoint, _log, cancellationToken))
        {
            return;
        }

        _counters.Increment(RuntimeCounter.GamePacketsOut);

        if (_verbose)
        {
            _log?.Invoke($"hub forwarded {decodedBytesWritten} bytes from edge {remoteEdgeEndPoint} to game for session {session.SessionId}");
        }
    }

    private void HandleClose(IPEndPoint remoteEdgeEndPoint, LaneZstdFrameHeader header, HubSessionManager sessionManager)
    {
        if (!sessionManager.TryGetOwnedSession(header.SessionId, remoteEdgeEndPoint, out _, out var isSenderMismatch))
        {
            _counters.Increment(isSenderMismatch ? RuntimeCounter.SessionSenderMismatch : RuntimeCounter.UnknownSession);
            return;
        }

        if (!sessionManager.TryRemoveOwnedSession(header.SessionId, remoteEdgeEndPoint, out var session) || session is null)
        {
            _counters.Increment(RuntimeCounter.UnknownSession);
            return;
        }

        _counters.Increment(RuntimeCounter.SessionsClosed);
        _counters.ChangeActiveSessions(-1);

        if (_verbose)
        {
            _log?.Invoke($"hub closed session {header.SessionId} from edge {remoteEdgeEndPoint}");
        }
    }

    private async Task RunSessionLoopAsync(
        HubSession session,
        Socket tunnelSocket,
        IPEndPoint gameEndPoint,
        RuntimeBufferSizing bufferSizing,
        CancellationToken cancellationToken)
    {
        var receiveBuffer = new byte[bufferSizing.MaxPayloadSize];
        var encodedPayloadBuffer = new byte[bufferSizing.MaxCompressedPayloadSize];
        var frameBuffer = new byte[bufferSizing.MaxPacketSize];
        using var encoder = new PayloadEncoder(_config.Runtime, bufferSizing);

        while (!cancellationToken.IsCancellationRequested)
        {
            var receiveResult = await UdpSocketIO.TryReceiveFromAsync(
                session.GameSocket,
                receiveBuffer,
                CreateReceiveEndPoint(_config.GameEndpoint.Address),
                _log,
                cancellationToken);

            if (receiveResult is not UdpSocketReceiveResult received)
            {
                if (cancellationToken.IsCancellationRequested || session.IsClosed)
                {
                    break;
                }

                continue;
            }

            _counters.Increment(RuntimeCounter.GamePacketsIn);

            if (received.RemoteEndPoint is not IPEndPoint remoteGameEndPoint || !remoteGameEndPoint.Equals(gameEndPoint))
            {
                continue;
            }

            session.Touch();

            var payloadLength = received.ReceivedBytes;
            var encodeResult = encoder.Encode(receiveBuffer.AsSpan(0, payloadLength), encodedPayloadBuffer, out var encodedBytesWritten);
            if (encodeResult.IsOversizeDrop)
            {
                _counters.Increment(RuntimeCounter.OversizeDrop);
                continue;
            }

            var dataFrame = new DataFrame(session.SessionId, (ushort)payloadLength, encodeResult.IsCompressed);
            if (!LaneZstdFrameCodec.TryWriteData(dataFrame, encodedPayloadBuffer.AsSpan(0, encodedBytesWritten), frameBuffer, out var frameBytesWritten))
            {
                _counters.Increment(RuntimeCounter.ProtocolError);
                continue;
            }

            if (!await UdpSocketIO.TrySendAsync(tunnelSocket, frameBuffer.AsMemory(0, frameBytesWritten), session.RemoteEdgeEndPoint, _log, cancellationToken))
            {
                continue;
            }

            _counters.Increment(RuntimeCounter.HubPacketsOut);
            _counters.Increment(encodeResult.IsCompressed ? RuntimeCounter.CompressedFramesOut : RuntimeCounter.RawFramesOut);
            _counters.Increment(RuntimeCounter.RawBytesIn, payloadLength);
            _counters.Increment(RuntimeCounter.FramedBytesOut, frameBytesWritten);

            if (_verbose)
            {
                _log?.Invoke($"hub forwarded {payloadLength} bytes from game to edge {session.RemoteEdgeEndPoint} for session {session.SessionId}");
            }
        }
    }

    private async Task RunTimeoutLoopAsync(Socket tunnelSocket, HubSessionManager sessionManager, CancellationToken cancellationToken)
    {
        var idleTimeout = TimeSpan.FromSeconds(_config.SessionIdleTimeoutSeconds);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var expiredSessions = sessionManager.RemoveExpiredSessions(idleTimeout);
            if (expiredSessions.Count == 0)
            {
                continue;
            }

            await CloseSessionsAsync(tunnelSocket, expiredSessions, timedOut: true, countClosed: false, cancellationToken);
        }
    }

    private async Task CloseSessionsAsync(Socket tunnelSocket, IReadOnlyList<HubSession> sessions, bool timedOut, bool countClosed, CancellationToken cancellationToken)
    {
        var closeBuffer = new byte[ProtocolConstants.HeaderSize];

        foreach (var session in sessions)
        {
            if (LaneZstdFrameCodec.TryWriteClose(new CloseFrame(session.SessionId), closeBuffer, out var closeBytesWritten))
            {
                if (await UdpSocketIO.TrySendAsync(tunnelSocket, closeBuffer[..closeBytesWritten].ToArray(), session.RemoteEdgeEndPoint, _log, cancellationToken))
                {
                    _counters.Increment(RuntimeCounter.HubPacketsOut);
                }
            }

            if (timedOut)
            {
                _counters.Increment(RuntimeCounter.SessionsTimedOut);
            }

            if (countClosed)
            {
                _counters.Increment(RuntimeCounter.SessionsClosed);
            }

            _counters.ChangeActiveSessions(-1);

            if (_verbose)
            {
                _log?.Invoke($"hub removed session {session.SessionId} for edge {session.RemoteEdgeEndPoint}{(timedOut ? " after idle timeout" : string.Empty)}");
            }
        }
    }

    private void StartSessionLoop(HubSession session, Socket tunnelSocket, IPEndPoint gameEndPoint, RuntimeBufferSizing bufferSizing, CancellationToken cancellationToken)
    {
        var task = RunSessionLoopAsync(session, tunnelSocket, gameEndPoint, bufferSizing, cancellationToken);
        lock (_sessionTaskSync)
        {
            _sessionTasks.Add(task);
        }
    }

    private async Task WaitForSessionTasksAsync()
    {
        Task[] tasks;
        lock (_sessionTaskSync)
        {
            tasks = _sessionTasks.ToArray();
        }

        if (tasks.Length == 0)
        {
            return;
        }

        await Task.WhenAll(tasks);
    }

    private static Socket CreateBoundSocket(UdpEndpoint endpoint)
    {
        var socket = new Socket(endpoint.Address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(ToIPEndPoint(endpoint));
        return socket;
    }

    private static IPEndPoint ToIPEndPoint(UdpEndpoint endpoint) => new(endpoint.Address, endpoint.Port);

    private static EndPoint CreateReceiveEndPoint(IPAddress bindAddress) =>
        bindAddress.AddressFamily == AddressFamily.InterNetworkV6
            ? new IPEndPoint(IPAddress.IPv6Any, 0)
            : new IPEndPoint(IPAddress.Any, 0);
}

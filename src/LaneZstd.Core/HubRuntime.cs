using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

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
        var tunnelReceiveChannel = UdpSocketIO.CreateDatagramChannel(_config.Runtime.ReceiveQueueCapacity, singleReader: true);
        var tunnelDataChannels = CreateTunnelDataChannels();
        var tunnelDataWriters = tunnelDataChannels.Select(static channel => channel.Writer).ToArray();
        var tunnelDataWorkers = tunnelDataChannels
            .Select(channel => RunTunnelDataWorkerAsync(channel.Reader, tunnelSocket, gameEndPoint, bufferSizing, sessionManager, cancellationToken))
            .ToArray();

        _log?.Invoke($"hub tunnel listening on {_config.BindEndpoint}; game target {_config.GameEndpoint}; session ports {_config.SessionPortRange}");

        try
        {
            await Task.WhenAll(
                RuntimeStatsReporter.RunPeriodicAsync("hub", _counters, _config.MaxSessions, _config.Runtime.StatsIntervalSeconds, _log, cancellationToken),
                UdpSocketIO.RunReceivePumpAsync(tunnelSocket, bufferSizing.MaxTunnelReceiveBufferSize, CreateReceiveEndPoint(_config.BindEndpoint.Address), tunnelReceiveChannel.Writer, _counters, _log, cancellationToken),
                RunTunnelDispatchLoopAsync(tunnelReceiveChannel.Reader, tunnelDataWriters, tunnelSocket, gameEndPoint, bufferSizing, sessionManager, cancellationToken),
                Task.WhenAll(tunnelDataWorkers),
                RunTimeoutLoopAsync(tunnelSocket, sessionManager, cancellationToken));
        }
        finally
        {
            await CloseSessionsAsync(tunnelSocket, sessionManager.RemoveAllSessions(), timedOut: false, countClosed: true, CancellationToken.None);
            await WaitForSessionTasksAsync();
            RuntimeStatsReporter.LogFinal("hub", _counters, _config.MaxSessions, _log);
        }
    }

    private Channel<TunnelDatagramWorkItem>[] CreateTunnelDataChannels()
    {
        var workerCount = Math.Max(1, _config.Runtime.ReceiveWorkerCount);
        var channels = new Channel<TunnelDatagramWorkItem>[workerCount];
        for (var index = 0; index < channels.Length; index++)
        {
            channels[index] = Channel.CreateBounded<TunnelDatagramWorkItem>(new BoundedChannelOptions(_config.Runtime.ReceiveQueueCapacity)
            {
                SingleWriter = true,
                SingleReader = true,
                FullMode = BoundedChannelFullMode.Wait,
            });
        }

        return channels;
    }

    private async Task RunTunnelDispatchLoopAsync(
        ChannelReader<PooledDatagram> reader,
        ChannelWriter<TunnelDatagramWorkItem>[] tunnelDataWriters,
        Socket tunnelSocket,
        IPEndPoint gameEndPoint,
        RuntimeBufferSizing bufferSizing,
        HubSessionManager sessionManager,
        CancellationToken cancellationToken)
    {
        try
        {
            while (await reader.WaitToReadAsync(cancellationToken))
            {
                while (reader.TryRead(out var received))
                {
                    _counters.Increment(RuntimeCounter.QueueDequeued);

                    _counters.Increment(RuntimeCounter.HubPacketsIn);

                    if (!LaneZstdFrameCodec.TryRead(received.Span, out var header, out _, out _))
                    {
                        using (received)
                        {
                            _counters.Increment(RuntimeCounter.ProtocolError);
                        }

                        continue;
                    }

                    switch (header.FrameType)
                    {
                        case FrameType.Register:
                            using (received)
                            {
                                await HandleRegisterAsync(tunnelSocket, received.RemoteEndPoint, gameEndPoint, bufferSizing, sessionManager, cancellationToken);
                            }

                            break;
                        case FrameType.Data:
                        case FrameType.Close:
                            RouteTunnelWorkItem(received, header, tunnelDataWriters);
                            break;
                        default:
                            using (received)
                            {
                                _counters.Increment(RuntimeCounter.ProtocolError);
                            }

                            break;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            foreach (var writer in tunnelDataWriters)
            {
                writer.TryComplete();
            }

            _counters.Increment(RuntimeCounter.QueueCompleted, tunnelDataWriters.Length);
        }
    }

    private void RouteTunnelWorkItem(PooledDatagram received, LaneZstdFrameHeader header, ChannelWriter<TunnelDatagramWorkItem>[] tunnelDataWriters)
    {
        var workerIndex = GetTunnelWorkerIndex(header.SessionId, tunnelDataWriters.Length);
        var workItem = new TunnelDatagramWorkItem(received, header);
        if (tunnelDataWriters[workerIndex].TryWrite(workItem))
        {
            _counters.Increment(RuntimeCounter.QueueEnqueued);
            return;
        }

        workItem.Dispose();
        _counters.Increment(RuntimeCounter.QueueDropped);
    }

    private async Task RunTunnelDataWorkerAsync(
        ChannelReader<TunnelDatagramWorkItem> reader,
        Socket tunnelSocket,
        IPEndPoint gameEndPoint,
        RuntimeBufferSizing bufferSizing,
        HubSessionManager sessionManager,
        CancellationToken cancellationToken)
    {
        var decodeBuffer = new byte[bufferSizing.MaxDatagramPayloadSize];
        using var decoder = new PayloadDecoder();

        try
        {
            while (await reader.WaitToReadAsync(cancellationToken))
            {
                while (reader.TryRead(out var workItem))
                {
                    _counters.Increment(RuntimeCounter.QueueDequeued);

                    using (workItem)
                    {
                        switch (workItem.Header.FrameType)
                        {
                            case FrameType.Data:
                                await HandleDataAsync(
                                    tunnelSocket,
                                    workItem.Datagram.RemoteEndPoint,
                                    gameEndPoint,
                                    workItem.Header,
                                    workItem.BodyMemory,
                                    decodeBuffer,
                                    decoder,
                                    sessionManager,
                                    cancellationToken);
                                break;
                            case FrameType.Close:
                                HandleClose(workItem.Datagram.RemoteEndPoint, workItem.Header, sessionManager);
                                break;
                            default:
                                _counters.Increment(RuntimeCounter.ProtocolError);
                                break;
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
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

        var decodeStartedAt = Stopwatch.GetTimestamp();
        var decoded = decoder.TryDecode(header, body.Span, decodeBuffer, out var decodedBytesWritten);
        _counters.Increment(RuntimeCounter.DecodeOperations);
        _counters.Increment(RuntimeCounter.DecodeElapsedTicks, Stopwatch.GetTimestamp() - decodeStartedAt);
        if (!decoded)
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

    private Task RunSessionLoopAsync(
        HubSession session,
        Socket tunnelSocket,
        IPEndPoint gameEndPoint,
        RuntimeBufferSizing bufferSizing,
        CancellationToken cancellationToken)
    {
        var gameChannel = UdpSocketIO.CreateDatagramChannel(_config.Runtime.ReceiveQueueCapacity, singleReader: true);
        return Task.WhenAll(
            UdpSocketIO.RunReceivePumpAsync(session.GameSocket, bufferSizing.MaxDatagramPayloadSize, CreateReceiveEndPoint(_config.GameEndpoint.Address), gameChannel.Writer, _counters, _log, cancellationToken),
            RunSessionProcessLoopAsync(session, gameChannel.Reader, tunnelSocket, gameEndPoint, bufferSizing, cancellationToken));
    }

    private async Task RunSessionProcessLoopAsync(
        HubSession session,
        ChannelReader<PooledDatagram> reader,
        Socket tunnelSocket,
        IPEndPoint gameEndPoint,
        RuntimeBufferSizing bufferSizing,
        CancellationToken cancellationToken)
    {
        var receiveBuffer = new byte[bufferSizing.MaxDatagramPayloadSize];
        var encodedPayloadBuffer = new byte[bufferSizing.MaxCompressedDatagramSize];
        var frameBuffer = new byte[bufferSizing.MaxPacketSize];
        using var encoder = new PayloadEncoder(_config.Runtime, bufferSizing);

        try
        {
            while (await reader.WaitToReadAsync(cancellationToken))
            {
                while (reader.TryRead(out var received))
                {
                    _counters.Increment(RuntimeCounter.QueueDequeued);

                    using (received)
                    {
                        _counters.Increment(RuntimeCounter.GamePacketsIn);

                        if (!received.RemoteEndPoint.Equals(gameEndPoint))
                        {
                            continue;
                        }

                        session.Touch();
                        received.Span.CopyTo(receiveBuffer);
                        var payloadLength = received.Length;
                        var encodeStartedAt = Stopwatch.GetTimestamp();
                        var encodeResult = encoder.Encode(receiveBuffer.AsSpan(0, payloadLength), encodedPayloadBuffer, out var encodedBytesWritten);
                        _counters.Increment(RuntimeCounter.EncodeOperations);
                        _counters.Increment(RuntimeCounter.EncodeElapsedTicks, Stopwatch.GetTimestamp() - encodeStartedAt);
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
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
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

    private static int GetTunnelWorkerIndex(SessionId sessionId, int workerCount)
    {
        return workerCount <= 1 ? 0 : (int)(sessionId.Value % (uint)workerCount);
    }

    private static Socket CreateBoundSocket(UdpEndpoint endpoint)
    {
        var socket = new Socket(endpoint.Address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        UdpSocketIO.ConfigureBuffers(socket);
        socket.Bind(ToIPEndPoint(endpoint));
        return socket;
    }

    private static IPEndPoint ToIPEndPoint(UdpEndpoint endpoint) => new(endpoint.Address, endpoint.Port);

    private static EndPoint CreateReceiveEndPoint(IPAddress bindAddress) =>
        bindAddress.AddressFamily == AddressFamily.InterNetworkV6
            ? new IPEndPoint(IPAddress.IPv6Any, 0)
            : new IPEndPoint(IPAddress.Any, 0);
}

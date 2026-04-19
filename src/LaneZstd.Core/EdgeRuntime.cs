using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using LaneZstd.Protocol;

namespace LaneZstd.Core;

public sealed class EdgeRuntime
{
    private readonly EdgeConfig _config;
    private readonly RuntimeCounters _counters;
    private readonly Action<string>? _log;
    private readonly bool _verbose;
    private readonly object _sync = new();
    private SessionId _sessionId = SessionId.None;
    private IPEndPoint? _clientEndPoint;

    public EdgeRuntime(EdgeConfig config, RuntimeCounters? counters = null, Action<string>? log = null, bool verbose = false)
    {
        _config = config;
        _counters = counters ?? new RuntimeCounters();
        _log = log;
        _verbose = verbose;
    }

    public RuntimeCounters Counters => _counters;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var tunnelSocket = CreateBoundSocket(_config.BindEndpoint);
        var gameSocket = CreateBoundSocket(_config.GameListenEndpoint);
        var hubEndPoint = ToIPEndPoint(_config.HubEndpoint);
        var bufferSizing = RuntimeBufferSizing.Create(_config.Runtime.MaxPacketSize);
        var gameChannel = UdpSocketIO.CreateDatagramChannel(_config.Runtime.ReceiveQueueCapacity, singleReader: _config.Runtime.ReceiveWorkerCount == 1);
        var tunnelChannel = UdpSocketIO.CreateDatagramChannel(_config.Runtime.ReceiveQueueCapacity, singleReader: true);

        _log?.Invoke($"edge tunnel listening on {_config.BindEndpoint}; game listening on {_config.GameListenEndpoint}; hub {hubEndPoint}");

        try
        {
            await SendRegisterAsync(tunnelSocket, hubEndPoint, cancellationToken);

            await Task.WhenAll(
                RuntimeStatsReporter.RunPeriodicAsync("edge", _counters, maxSessions: 1, _config.Runtime.StatsIntervalSeconds, _log, cancellationToken),
                UdpSocketIO.RunReceivePumpAsync(gameSocket, bufferSizing.MaxPayloadSize, CreateReceiveEndPoint(_config.GameListenEndpoint.Address), gameChannel.Writer, _counters, _log, cancellationToken),
                UdpSocketIO.RunReceivePumpAsync(tunnelSocket, bufferSizing.MaxReceiveBufferSize, CreateReceiveEndPoint(_config.BindEndpoint.Address), tunnelChannel.Writer, _counters, _log, cancellationToken),
                RunGameWorkersAsync(gameChannel.Reader, tunnelSocket, hubEndPoint, bufferSizing, cancellationToken),
                RunTunnelProcessLoopAsync(tunnelChannel.Reader, gameSocket, hubEndPoint, bufferSizing, cancellationToken));
        }
        finally
        {
            await CloseActiveSessionAsync(tunnelSocket, hubEndPoint);
            RuntimeStatsReporter.LogFinal("edge", _counters, maxSessions: 1, _log);
            tunnelSocket.Dispose();
            gameSocket.Dispose();
        }
    }

    private async Task CloseActiveSessionAsync(Socket tunnelSocket, IPEndPoint hubEndPoint)
    {
        SessionId sessionId;

        lock (_sync)
        {
            if (_sessionId.IsEmpty)
            {
                return;
            }

            sessionId = _sessionId;
            _sessionId = SessionId.None;
            _counters.Increment(RuntimeCounter.SessionsClosed);
            _counters.ChangeActiveSessions(-1);
        }

        Span<byte> closeBuffer = stackalloc byte[ProtocolConstants.HeaderSize];
        if (LaneZstdFrameCodec.TryWriteClose(new CloseFrame(sessionId), closeBuffer, out var bytesWritten))
        {
            if (await UdpSocketIO.TrySendAsync(tunnelSocket, closeBuffer[..bytesWritten].ToArray(), hubEndPoint, _log, CancellationToken.None))
            {
                _counters.Increment(RuntimeCounter.HubPacketsOut);
            }
        }

        _log?.Invoke($"edge closed session {sessionId}");
    }

    private async Task SendRegisterAsync(Socket tunnelSocket, IPEndPoint hubEndPoint, CancellationToken cancellationToken)
    {
        Span<byte> frameBuffer = stackalloc byte[ProtocolConstants.HeaderSize];
        if (!LaneZstdFrameCodec.TryWriteRegister(new RegisterFrame(SessionId.None), frameBuffer, out var bytesWritten))
        {
            throw new InvalidOperationException("Failed to build register frame.");
        }

        if (await UdpSocketIO.TrySendAsync(tunnelSocket, frameBuffer[..bytesWritten].ToArray(), hubEndPoint, _log, cancellationToken))
        {
            _counters.Increment(RuntimeCounter.HubPacketsOut);
            if (_verbose)
            {
                _log?.Invoke("edge sent register frame to hub");
            }
        }
    }

    private Task RunGameWorkersAsync(
        ChannelReader<PooledDatagram> reader,
        Socket tunnelSocket,
        IPEndPoint hubEndPoint,
        RuntimeBufferSizing bufferSizing,
        CancellationToken cancellationToken)
    {
        var workerCount = Math.Max(1, _config.Runtime.ReceiveWorkerCount);
        var workers = new Task[workerCount];
        for (var index = 0; index < workerCount; index++)
        {
            workers[index] = RunGameProcessLoopAsync(reader, tunnelSocket, hubEndPoint, bufferSizing, cancellationToken);
        }

        return Task.WhenAll(workers);
    }

    private async Task RunGameProcessLoopAsync(
        ChannelReader<PooledDatagram> reader,
        Socket tunnelSocket,
        IPEndPoint hubEndPoint,
        RuntimeBufferSizing bufferSizing,
        CancellationToken cancellationToken)
    {
        var receiveBuffer = new byte[bufferSizing.MaxPayloadSize];
        var encodedPayloadBuffer = new byte[bufferSizing.MaxCompressedPayloadSize];
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
                        _counters.Increment(RuntimeCounter.EdgePacketsIn);

                        if (!TryLearnClient(received.RemoteEndPoint))
                        {
                            continue;
                        }

                        if (!TryGetSessionId(out var sessionId))
                        {
                            continue;
                        }

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

                        var dataFrame = new DataFrame(sessionId, (ushort)payloadLength, encodeResult.IsCompressed);
                        if (!LaneZstdFrameCodec.TryWriteData(dataFrame, encodedPayloadBuffer.AsSpan(0, encodedBytesWritten), frameBuffer, out var frameBytesWritten))
                        {
                            _counters.Increment(RuntimeCounter.ProtocolError);
                            continue;
                        }

                        if (!await UdpSocketIO.TrySendAsync(tunnelSocket, frameBuffer.AsMemory(0, frameBytesWritten), hubEndPoint, _log, cancellationToken))
                        {
                            continue;
                        }

                        _counters.Increment(RuntimeCounter.HubPacketsOut);
                        _counters.Increment(encodeResult.IsCompressed ? RuntimeCounter.CompressedFramesOut : RuntimeCounter.RawFramesOut);
                        _counters.Increment(RuntimeCounter.RawBytesIn, payloadLength);
                        _counters.Increment(RuntimeCounter.FramedBytesOut, frameBytesWritten);

                        if (_verbose)
                        {
                            _log?.Invoke($"edge forwarded {payloadLength} bytes to hub as {(encodeResult.IsCompressed ? "compressed" : "raw")} frame for session {sessionId}");
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task RunTunnelProcessLoopAsync(
        ChannelReader<PooledDatagram> reader,
        Socket gameSocket,
        IPEndPoint hubEndPoint,
        RuntimeBufferSizing bufferSizing,
        CancellationToken cancellationToken)
    {
        var receiveBuffer = new byte[bufferSizing.MaxReceiveBufferSize];
        var decodeBuffer = new byte[bufferSizing.MaxPayloadSize];
        using var decoder = new PayloadDecoder();

        try
        {
            while (await reader.WaitToReadAsync(cancellationToken))
            {
                while (reader.TryRead(out var received))
                {
                    _counters.Increment(RuntimeCounter.QueueDequeued);

                    using (received)
                    {
                        _counters.Increment(RuntimeCounter.HubPacketsIn);

                        if (!received.RemoteEndPoint.Equals(hubEndPoint))
                        {
                            _counters.Increment(RuntimeCounter.SessionSenderMismatch);
                            continue;
                        }

                        received.Span.CopyTo(receiveBuffer);
                        if (!LaneZstdFrameCodec.TryRead(receiveBuffer.AsSpan(0, received.Length), out var header, out var body, out _))
                        {
                            _counters.Increment(RuntimeCounter.ProtocolError);
                            continue;
                        }

                        switch (header.FrameType)
                        {
                            case FrameType.RegisterAck:
                                HandleRegisterAck(header, body);
                                break;
                            case FrameType.Data:
                                if (!TryPrepareInboundData(header, body, decoder, decodeBuffer, out var bytesWritten, out var clientEndPoint))
                                {
                                    break;
                                }

                                if (!await UdpSocketIO.TrySendAsync(gameSocket, decodeBuffer.AsMemory(0, bytesWritten), clientEndPoint, _log, cancellationToken))
                                {
                                    break;
                                }

                                _counters.Increment(RuntimeCounter.EdgePacketsOut);

                                if (_verbose)
                                {
                                    _log?.Invoke($"edge forwarded {bytesWritten} bytes from hub to client for session {header.SessionId}");
                                }

                                break;
                            case FrameType.Close:
                                HandleClose(header);
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

    private void HandleRegisterAck(LaneZstdFrameHeader header, ReadOnlySpan<byte> body)
    {
        if (TryGetSessionId(out var currentSessionId))
        {
            if (currentSessionId != header.SessionId)
            {
                _counters.Increment(RuntimeCounter.UnknownSession);
            }

            return;
        }

        var allocatedPort = BinaryPrimitives.ReadUInt16LittleEndian(body);
        lock (_sync)
        {
            if (_sessionId.IsEmpty)
            {
                _sessionId = header.SessionId;
                _counters.Increment(RuntimeCounter.SessionsCreated);
                _counters.ChangeActiveSessions(1);
                _log?.Invoke($"edge registered session {header.SessionId} with hub port {allocatedPort}");
            }
        }
    }

    private bool TryPrepareInboundData(
        LaneZstdFrameHeader header,
        ReadOnlySpan<byte> body,
        PayloadDecoder decoder,
        byte[] decodeBuffer,
        out int bytesWritten,
        out IPEndPoint clientEndPoint)
    {
        bytesWritten = 0;
        clientEndPoint = new IPEndPoint(IPAddress.Any, 0);

        if (!TryGetSessionId(out var sessionId) || sessionId != header.SessionId)
        {
            _counters.Increment(RuntimeCounter.UnknownSession);
            return false;
        }

        if (!TryGetClientEndPoint(out var learnedClientEndPoint))
        {
            return false;
        }

        var decodeStartedAt = Stopwatch.GetTimestamp();
        var decoded = decoder.TryDecode(header, body, decodeBuffer, out bytesWritten);
        _counters.Increment(RuntimeCounter.DecodeOperations);
        _counters.Increment(RuntimeCounter.DecodeElapsedTicks, Stopwatch.GetTimestamp() - decodeStartedAt);
        if (!decoded)
        {
            _counters.Increment(RuntimeCounter.DecompressError);
            return false;
        }

        clientEndPoint = learnedClientEndPoint;
        return true;
    }

    private void HandleClose(LaneZstdFrameHeader header)
    {
        lock (_sync)
        {
            if (_sessionId.IsEmpty || _sessionId != header.SessionId)
            {
                _counters.Increment(RuntimeCounter.UnknownSession);
                return;
            }

            _sessionId = SessionId.None;
            _counters.Increment(RuntimeCounter.SessionsClosed);
            _counters.ChangeActiveSessions(-1);
            _log?.Invoke($"edge closed session {header.SessionId}");
        }
    }

    private bool TryLearnClient(IPEndPoint clientEndPoint)
    {
        lock (_sync)
        {
            if (_clientEndPoint is null)
            {
                _clientEndPoint = clientEndPoint;
                return true;
            }

            return _clientEndPoint.Equals(clientEndPoint);
        }
    }

    private bool TryGetClientEndPoint(out IPEndPoint clientEndPoint)
    {
        lock (_sync)
        {
            clientEndPoint = _clientEndPoint!;
            return clientEndPoint is not null;
        }
    }

    private bool TryGetSessionId(out SessionId sessionId)
    {
        lock (_sync)
        {
            sessionId = _sessionId;
            return !sessionId.IsEmpty;
        }
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

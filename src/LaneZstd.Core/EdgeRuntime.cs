using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
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

        using var encoder = new PayloadEncoder(_config.Runtime, bufferSizing);
        using var decoder = new PayloadDecoder();

        _log?.Invoke($"edge tunnel listening on {_config.BindEndpoint}; game listening on {_config.GameListenEndpoint}; hub {hubEndPoint}");

        try
        {
            await SendRegisterAsync(tunnelSocket, hubEndPoint, cancellationToken);

            await Task.WhenAll(
                RuntimeStatsReporter.RunPeriodicAsync("edge", _counters, maxSessions: 1, _config.Runtime.StatsIntervalSeconds, _log, cancellationToken),
                RunGameLoopAsync(gameSocket, tunnelSocket, hubEndPoint, encoder, bufferSizing, cancellationToken),
                RunTunnelLoopAsync(tunnelSocket, gameSocket, hubEndPoint, decoder, bufferSizing, cancellationToken));
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

    private async Task RunGameLoopAsync(
        Socket gameSocket,
        Socket tunnelSocket,
        IPEndPoint hubEndPoint,
        PayloadEncoder encoder,
        RuntimeBufferSizing bufferSizing,
        CancellationToken cancellationToken)
    {
        var receiveBuffer = new byte[bufferSizing.MaxPayloadSize];
        var encodedPayloadBuffer = new byte[bufferSizing.MaxCompressedPayloadSize];
        var frameBuffer = new byte[bufferSizing.MaxPacketSize];

        while (!cancellationToken.IsCancellationRequested)
        {
            var receiveResult = await UdpSocketIO.TryReceiveFromAsync(
                gameSocket,
                receiveBuffer,
                CreateReceiveEndPoint(_config.GameListenEndpoint.Address),
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

            _counters.Increment(RuntimeCounter.EdgePacketsIn);

            if (received.RemoteEndPoint is not IPEndPoint clientEndPoint)
            {
                continue;
            }

            if (!TryLearnClient(clientEndPoint))
            {
                continue;
            }

            if (!TryGetSessionId(out var sessionId))
            {
                continue;
            }

            var payloadLength = received.ReceivedBytes;
            var encodeResult = encoder.Encode(receiveBuffer.AsSpan(0, payloadLength), encodedPayloadBuffer, out var encodedBytesWritten);

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

    private async Task RunTunnelLoopAsync(
        Socket tunnelSocket,
        Socket gameSocket,
        IPEndPoint hubEndPoint,
        PayloadDecoder decoder,
        RuntimeBufferSizing bufferSizing,
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

            if (received.RemoteEndPoint is not IPEndPoint remoteEndPoint)
            {
                continue;
            }

            if (!remoteEndPoint.Equals(hubEndPoint))
            {
                _counters.Increment(RuntimeCounter.SessionSenderMismatch);
                continue;
            }

            if (!LaneZstdFrameCodec.TryRead(receiveBuffer.AsSpan(0, received.ReceivedBytes), out var header, out var body, out _))
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

        if (!decoder.TryDecode(header, body, decodeBuffer, out bytesWritten))
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
        socket.Bind(ToIPEndPoint(endpoint));
        return socket;
    }

    private static IPEndPoint ToIPEndPoint(UdpEndpoint endpoint) => new(endpoint.Address, endpoint.Port);

    private static EndPoint CreateReceiveEndPoint(IPAddress bindAddress) =>
        bindAddress.AddressFamily == AddressFamily.InterNetworkV6
            ? new IPEndPoint(IPAddress.IPv6Any, 0)
            : new IPEndPoint(IPAddress.Any, 0);
}

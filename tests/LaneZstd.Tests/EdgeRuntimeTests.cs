using System.Net;
using System.Net.Sockets;
using System.Text;
using LaneZstd.Core;
using LaneZstd.Protocol;

namespace LaneZstd.Tests;

public sealed class EdgeRuntimeTests
{
    [Fact]
    public async Task EdgeRuntime_RegistersAndRelaysTrafficAfterAck()
    {
        var hubPort = ReserveUdpPort();
        var edgeBindPort = ReserveUdpPort();
        var edgeGamePort = ReserveUdpPort();
        var config = CreateConfig(edgeBindPort, hubPort, edgeGamePort);
        var counters = new RuntimeCounters();
        using var hubSocket = BindLoopbackSocket(hubPort);
        using var clientSocket = BindLoopbackSocket();
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runtime = new EdgeRuntime(config, counters);

        var runtimeTask = runtime.RunAsync(cancellationSource.Token);

        var registerDatagram = await ReceiveAsync(hubSocket, cancellationSource.Token);
        Assert.True(LaneZstdFrameCodec.TryRead(registerDatagram.Buffer, out var registerHeader, out _, out _));
        Assert.Equal(FrameType.Register, registerHeader.FrameType);
        Assert.True(registerHeader.SessionId.IsEmpty);

        var sessionId = new SessionId(1234);
        var ackBuffer = new byte[ProtocolConstants.HeaderSize + sizeof(ushort)];
        Assert.True(LaneZstdFrameCodec.TryWriteRegisterAck(new RegisterAckFrame(sessionId, 41000), ackBuffer, out var ackBytesWritten));
        await hubSocket.SendToAsync(ackBuffer.AsMemory(0, ackBytesWritten), SocketFlags.None, new IPEndPoint(IPAddress.Loopback, edgeBindPort), cancellationSource.Token);
        await WaitForAsync(() => counters.Snapshot(maxSessions: 1).ActiveSessions == 1, cancellationSource.Token);

        var outboundPayload = Encoding.ASCII.GetBytes(new string('A', 128));
        await clientSocket.SendToAsync(outboundPayload, SocketFlags.None, new IPEndPoint(IPAddress.Loopback, edgeGamePort), cancellationSource.Token);

        var forwardedDatagram = await ReceiveAsync(hubSocket, cancellationSource.Token);
        Assert.True(LaneZstdFrameCodec.TryRead(forwardedDatagram.Buffer, out var dataHeader, out var dataBody, out _));
        Assert.Equal(FrameType.Data, dataHeader.FrameType);
        Assert.Equal(sessionId, dataHeader.SessionId);

        using var decoder = new PayloadDecoder();
        var decoded = new byte[outboundPayload.Length];
        Assert.True(decoder.TryDecode(dataHeader, dataBody, decoded, out var decodedBytes));
        Assert.Equal(outboundPayload, decoded[..decodedBytes]);

        var inboundPayload = Encoding.ASCII.GetBytes("server-reply");
        var inboundFrameBuffer = new byte[config.Runtime.MaxPacketSize];
        Assert.True(LaneZstdFrameCodec.TryWriteData(new DataFrame(sessionId, (ushort)inboundPayload.Length, false), inboundPayload, inboundFrameBuffer, out var inboundFrameBytes));
        await hubSocket.SendToAsync(inboundFrameBuffer.AsMemory(0, inboundFrameBytes), SocketFlags.None, new IPEndPoint(IPAddress.Loopback, edgeBindPort), cancellationSource.Token);

        var clientReceive = await ReceiveAsync(clientSocket, cancellationSource.Token);
        Assert.Equal(inboundPayload, clientReceive.Buffer);

        cancellationSource.Cancel();
        await runtimeTask;

        var snapshot = counters.Snapshot(maxSessions: 1);
        Assert.Equal(1, snapshot.SessionsCreated);
        Assert.Equal(1, snapshot.SessionsClosed);
        Assert.Equal(0, snapshot.ActiveSessions);
        Assert.Equal(1, snapshot.EdgePacketsIn);
        Assert.Equal(1, snapshot.EdgePacketsOut);
        Assert.Equal(2, snapshot.HubPacketsIn);
        Assert.Equal(3, snapshot.HubPacketsOut);
    }

    [Fact]
    public async Task EdgeRuntime_DropsPreregistrationTrafficRejectsUnexpectedSenderAndClosesSession()
    {
        var hubPort = ReserveUdpPort();
        var edgeBindPort = ReserveUdpPort();
        var edgeGamePort = ReserveUdpPort();
        var config = CreateConfig(edgeBindPort, hubPort, edgeGamePort);
        var counters = new RuntimeCounters();
        using var hubSocket = BindLoopbackSocket(hubPort);
        using var unexpectedSocket = BindLoopbackSocket();
        using var clientSocket = BindLoopbackSocket();
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runtime = new EdgeRuntime(config, counters);

        var runtimeTask = runtime.RunAsync(cancellationSource.Token);

        _ = await ReceiveAsync(hubSocket, cancellationSource.Token);

        var prerRegistrationPayload = Encoding.ASCII.GetBytes("before-ack");
        await clientSocket.SendToAsync(prerRegistrationPayload, SocketFlags.None, new IPEndPoint(IPAddress.Loopback, edgeGamePort), cancellationSource.Token);
        await AssertNoDatagramAsync(hubSocket, cancellationSource.Token);

        var sessionId = new SessionId(5678);
        var ackBuffer = new byte[ProtocolConstants.HeaderSize + sizeof(ushort)];
        Assert.True(LaneZstdFrameCodec.TryWriteRegisterAck(new RegisterAckFrame(sessionId, 41001), ackBuffer, out var ackBytesWritten));
        await hubSocket.SendToAsync(ackBuffer.AsMemory(0, ackBytesWritten), SocketFlags.None, new IPEndPoint(IPAddress.Loopback, edgeBindPort), cancellationSource.Token);
        await WaitForAsync(() => counters.Snapshot(maxSessions: 1).ActiveSessions == 1, cancellationSource.Token);

        var badSenderPayload = Encoding.ASCII.GetBytes("intruder");
        var badSenderFrame = new byte[ProtocolConstants.HeaderSize + badSenderPayload.Length];
        Assert.True(LaneZstdFrameCodec.TryWriteData(new DataFrame(sessionId, (ushort)badSenderPayload.Length, false), badSenderPayload, badSenderFrame, out var badSenderBytes));
        await unexpectedSocket.SendToAsync(badSenderFrame.AsMemory(0, badSenderBytes), SocketFlags.None, new IPEndPoint(IPAddress.Loopback, edgeBindPort), cancellationSource.Token);
        await AssertNoDatagramAsync(clientSocket, cancellationSource.Token);

        var closeBuffer = new byte[ProtocolConstants.HeaderSize];
        Assert.True(LaneZstdFrameCodec.TryWriteClose(new CloseFrame(sessionId), closeBuffer, out var closeBytesWritten));
        await hubSocket.SendToAsync(closeBuffer.AsMemory(0, closeBytesWritten), SocketFlags.None, new IPEndPoint(IPAddress.Loopback, edgeBindPort), cancellationSource.Token);
        await WaitForAsync(() => counters.Snapshot(maxSessions: 1).ActiveSessions == 0, cancellationSource.Token);

        var afterClosePayload = Encoding.ASCII.GetBytes("after-close");
        await clientSocket.SendToAsync(afterClosePayload, SocketFlags.None, new IPEndPoint(IPAddress.Loopback, edgeGamePort), cancellationSource.Token);
        await AssertNoDatagramAsync(hubSocket, cancellationSource.Token);

        cancellationSource.Cancel();
        await runtimeTask;

        var snapshot = counters.Snapshot(maxSessions: 1);
        Assert.Equal(1, snapshot.SessionsCreated);
        Assert.Equal(1, snapshot.SessionsClosed);
        Assert.Equal(0, snapshot.ActiveSessions);
        Assert.Equal(1, snapshot.SessionSenderMismatch);
        Assert.Equal(0, snapshot.UnknownSession);
        Assert.Equal(0, snapshot.ProtocolError);
    }

    [Fact]
    public async Task EdgeRuntime_LogsPeriodicAndFinalStatsAndClosesSessionOnShutdown()
    {
        var hubPort = ReserveUdpPort();
        var edgeBindPort = ReserveUdpPort();
        var edgeGamePort = ReserveUdpPort();
        var config = new EdgeConfig(
            new UdpEndpoint(IPAddress.Loopback, edgeBindPort),
            new UdpEndpoint(IPAddress.Loopback, hubPort),
            new UdpEndpoint(IPAddress.Loopback, edgeGamePort),
            new RuntimeOptions(CompressThreshold: 32, CompressionLevel: 3, MaxPacketSize: 1200, StatsIntervalSeconds: 1));
        var counters = new RuntimeCounters();
        var logs = new List<string>();
        using var hubSocket = BindLoopbackSocket(hubPort);
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runtime = new EdgeRuntime(config, counters, logs.Add);

        var runtimeTask = runtime.RunAsync(cancellationSource.Token);

        _ = await ReceiveAsync(hubSocket, cancellationSource.Token);

        var sessionId = new SessionId(7777);
        var ackBuffer = new byte[ProtocolConstants.HeaderSize + sizeof(ushort)];
        Assert.True(LaneZstdFrameCodec.TryWriteRegisterAck(new RegisterAckFrame(sessionId, 41002), ackBuffer, out var ackBytesWritten));
        await hubSocket.SendToAsync(ackBuffer.AsMemory(0, ackBytesWritten), SocketFlags.None, new IPEndPoint(IPAddress.Loopback, edgeBindPort), cancellationSource.Token);
        await WaitForAsync(() => counters.Snapshot(maxSessions: 1).ActiveSessions == 1, cancellationSource.Token);

        await WaitForAsync(() => logs.Any(line => line.Contains("edge stats ") && !line.Contains("final")), cancellationSource.Token);

        cancellationSource.Cancel();
        var closeDatagram = await ReceiveAsync(hubSocket, CancellationToken.None);
        Assert.True(LaneZstdFrameCodec.TryRead(closeDatagram.Buffer, out var closeHeader, out _, out _));
        Assert.Equal(FrameType.Close, closeHeader.FrameType);
        Assert.Equal(sessionId, closeHeader.SessionId);

        await runtimeTask;

        Assert.Contains(logs, line => line.Contains("edge stats final"));

        var snapshot = counters.Snapshot(maxSessions: 1);
        Assert.Equal(1, snapshot.SessionsClosed);
        Assert.Equal(0, snapshot.ActiveSessions);
    }

    private static EdgeConfig CreateConfig(int edgeBindPort, int hubPort, int edgeGamePort)
    {
        return new EdgeConfig(
            new UdpEndpoint(IPAddress.Loopback, edgeBindPort),
            new UdpEndpoint(IPAddress.Loopback, hubPort),
            new UdpEndpoint(IPAddress.Loopback, edgeGamePort),
            new RuntimeOptions(CompressThreshold: 32, CompressionLevel: 3, MaxPacketSize: 1200, StatsIntervalSeconds: 0));
    }

    private static Socket BindLoopbackSocket(int? port = null)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, port ?? 0));
        return socket;
    }

    private static async Task<(byte[] Buffer, EndPoint RemoteEndPoint)> ReceiveAsync(Socket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[2048];
        var result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), cancellationToken);
        return (buffer[..result.ReceivedBytes], result.RemoteEndPoint);
    }

    private static async Task AssertNoDatagramAsync(Socket socket, CancellationToken cancellationToken)
    {
        using var shortTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        shortTimeout.CancelAfter(TimeSpan.FromMilliseconds(250));

        try
        {
            _ = await ReceiveAsync(socket, shortTimeout.Token);
            Assert.Fail("Expected no datagram but one was received.");
        }
        catch (OperationCanceledException) when (shortTimeout.IsCancellationRequested)
        {
        }
    }

    private static int ReserveUdpPort()
    {
        using var socket = BindLoopbackSocket();
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    private static async Task WaitForAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(20, cancellationToken);
        }
    }
}

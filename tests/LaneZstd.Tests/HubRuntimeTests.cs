using System.Net;
using System.Net.Sockets;
using System.Text;

using LaneZstd.Core;
using LaneZstd.Protocol;

namespace LaneZstd.Tests;

public sealed class HubRuntimeTests
{
    [Fact]
    public async Task HubRuntime_RegistersRelaysAndReusesExistingSession()
    {
        var hubPort = ReserveUdpPort();
        var gamePort = ReserveUdpPort();
        var sessionPortRange = ReserveUdpPortRange(1);
        var config = CreateConfig(hubPort, gamePort, sessionPortRange.startPort, sessionPortRange.endPort, idleTimeoutSeconds: 5, maxSessions: 2);
        var counters = new RuntimeCounters();
        using var edgeSocket = BindLoopbackSocket();
        using var gameSocket = BindLoopbackSocket(gamePort);
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runtime = new HubRuntime(config, counters);

        var runtimeTask = runtime.RunAsync(cancellationSource.Token);

        var registerBuffer = new byte[ProtocolConstants.HeaderSize];
        Assert.True(LaneZstdFrameCodec.TryWriteRegister(new RegisterFrame(SessionId.None), registerBuffer, out var registerBytesWritten));
        await edgeSocket.SendToAsync(registerBuffer.AsMemory(0, registerBytesWritten), SocketFlags.None, new IPEndPoint(IPAddress.Loopback, hubPort), cancellationSource.Token);

        var ackDatagram = await ReceiveAsync(edgeSocket, cancellationSource.Token);
        Assert.True(LaneZstdFrameCodec.TryRead(ackDatagram.Buffer, out var ackHeader, out var ackBody, out _));
        Assert.Equal(FrameType.RegisterAck, ackHeader.FrameType);
        var sessionId = ackHeader.SessionId;
        var allocatedPort = BitConverter.ToUInt16(ackBody);
        Assert.InRange(allocatedPort, sessionPortRange.startPort, sessionPortRange.endPort);
        await WaitForAsync(() => counters.Snapshot(maxSessions: 2).ActiveSessions == 1, cancellationSource.Token);

        var outboundPayload = Encoding.ASCII.GetBytes(new string('B', 128));
        var outboundFrameBuffer = new byte[config.Runtime.MaxPacketSize];
        Assert.True(LaneZstdFrameCodec.TryWriteData(new DataFrame(sessionId, (ushort)outboundPayload.Length, false), outboundPayload, outboundFrameBuffer, out var outboundFrameBytes));
        await edgeSocket.SendToAsync(outboundFrameBuffer.AsMemory(0, outboundFrameBytes), SocketFlags.None, new IPEndPoint(IPAddress.Loopback, hubPort), cancellationSource.Token);

        var gameReceive = await ReceiveAsync(gameSocket, cancellationSource.Token);
        Assert.Equal(outboundPayload, gameReceive.Buffer);
        var sessionGameEndPoint = Assert.IsType<IPEndPoint>(gameReceive.RemoteEndPoint);
        Assert.Equal(allocatedPort, sessionGameEndPoint.Port);

        var inboundPayload = Encoding.ASCII.GetBytes("game-reply");
        await gameSocket.SendToAsync(inboundPayload, SocketFlags.None, sessionGameEndPoint, cancellationSource.Token);

        var edgeReceive = await ReceiveAsync(edgeSocket, cancellationSource.Token);
        Assert.True(LaneZstdFrameCodec.TryRead(edgeReceive.Buffer, out var dataHeader, out var dataBody, out _));
        Assert.Equal(FrameType.Data, dataHeader.FrameType);
        Assert.Equal(sessionId, dataHeader.SessionId);

        using var decoder = new PayloadDecoder();
        var decoded = new byte[inboundPayload.Length];
        Assert.True(decoder.TryDecode(dataHeader, dataBody, decoded, out var decodedBytes));
        Assert.Equal(inboundPayload, decoded[..decodedBytes]);

        await edgeSocket.SendToAsync(registerBuffer.AsMemory(0, registerBytesWritten), SocketFlags.None, new IPEndPoint(IPAddress.Loopback, hubPort), cancellationSource.Token);
        var secondAckDatagram = await ReceiveAsync(edgeSocket, cancellationSource.Token);
        Assert.True(LaneZstdFrameCodec.TryRead(secondAckDatagram.Buffer, out var secondAckHeader, out var secondAckBody, out _));
        Assert.Equal(sessionId, secondAckHeader.SessionId);
        Assert.Equal(allocatedPort, BitConverter.ToUInt16(secondAckBody));

        cancellationSource.Cancel();
        await runtimeTask;

        var snapshot = counters.Snapshot(maxSessions: 2);
        Assert.Equal(1, snapshot.SessionsCreated);
        Assert.Equal(1, snapshot.SessionsClosed);
        Assert.Equal(0, snapshot.ActiveSessions);
        Assert.Equal(3, snapshot.HubPacketsIn);
        Assert.Equal(4, snapshot.HubPacketsOut);
        Assert.Equal(1, snapshot.GamePacketsIn);
        Assert.Equal(1, snapshot.GamePacketsOut);
    }

    [Fact]
    public async Task HubRuntime_HandlesExhaustionMismatchUnknownSessionAndTimeout()
    {
        var hubPort = ReserveUdpPort();
        var gamePort = ReserveUdpPort();
        var sessionPort = ReserveUdpPort();
        var config = CreateConfig(hubPort, gamePort, sessionPort, sessionPort, idleTimeoutSeconds: 1, maxSessions: 1);
        var counters = new RuntimeCounters();
        using var firstEdgeSocket = BindLoopbackSocket();
        using var secondEdgeSocket = BindLoopbackSocket();
        using var gameSocket = BindLoopbackSocket(gamePort);
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runtime = new HubRuntime(config, counters);

        var runtimeTask = runtime.RunAsync(cancellationSource.Token);

        var registerBuffer = new byte[ProtocolConstants.HeaderSize];
        Assert.True(LaneZstdFrameCodec.TryWriteRegister(new RegisterFrame(SessionId.None), registerBuffer, out var registerBytesWritten));
        await firstEdgeSocket.SendToAsync(registerBuffer.AsMemory(0, registerBytesWritten), SocketFlags.None, new IPEndPoint(IPAddress.Loopback, hubPort), cancellationSource.Token);

        var ackDatagram = await ReceiveAsync(firstEdgeSocket, cancellationSource.Token);
        Assert.True(LaneZstdFrameCodec.TryRead(ackDatagram.Buffer, out var ackHeader, out _, out _));
        var sessionId = ackHeader.SessionId;
        await WaitForAsync(() => counters.Snapshot(maxSessions: 1).ActiveSessions == 1, cancellationSource.Token);

        await secondEdgeSocket.SendToAsync(registerBuffer.AsMemory(0, registerBytesWritten), SocketFlags.None, new IPEndPoint(IPAddress.Loopback, hubPort), cancellationSource.Token);
        await WaitForAsync(() => counters.Snapshot(maxSessions: 1).PortPoolExhausted == 1, cancellationSource.Token);
        await AssertNoDatagramAsync(secondEdgeSocket, cancellationSource.Token);

        var unknownPayload = Encoding.ASCII.GetBytes("unknown");
        var unknownFrame = new byte[ProtocolConstants.HeaderSize + unknownPayload.Length];
        Assert.True(LaneZstdFrameCodec.TryWriteData(new DataFrame(new SessionId(999999), (ushort)unknownPayload.Length, false), unknownPayload, unknownFrame, out var unknownFrameBytes));
        await firstEdgeSocket.SendToAsync(unknownFrame.AsMemory(0, unknownFrameBytes), SocketFlags.None, new IPEndPoint(IPAddress.Loopback, hubPort), cancellationSource.Token);

        var mismatchFrame = new byte[ProtocolConstants.HeaderSize + unknownPayload.Length];
        Assert.True(LaneZstdFrameCodec.TryWriteData(new DataFrame(sessionId, (ushort)unknownPayload.Length, false), unknownPayload, mismatchFrame, out var mismatchFrameBytes));
        await secondEdgeSocket.SendToAsync(mismatchFrame.AsMemory(0, mismatchFrameBytes), SocketFlags.None, new IPEndPoint(IPAddress.Loopback, hubPort), cancellationSource.Token);

        await WaitForAsync(() =>
        {
            var snapshot = counters.Snapshot(maxSessions: 1);
            return snapshot.UnknownSession == 1 && snapshot.SessionSenderMismatch == 1;
        }, cancellationSource.Token);

        var closeDatagram = await ReceiveAsync(firstEdgeSocket, cancellationSource.Token);
        Assert.True(LaneZstdFrameCodec.TryRead(closeDatagram.Buffer, out var closeHeader, out var closeBody, out _));
        Assert.Equal(FrameType.Close, closeHeader.FrameType);
        Assert.Equal(sessionId, closeHeader.SessionId);
        Assert.Empty(closeBody.ToArray());
        await WaitForAsync(() => counters.Snapshot(maxSessions: 1).ActiveSessions == 0, cancellationSource.Token);

        cancellationSource.Cancel();
        await runtimeTask;

        var finalSnapshot = counters.Snapshot(maxSessions: 1);
        Assert.Equal(1, finalSnapshot.SessionsCreated);
        Assert.Equal(1, finalSnapshot.SessionsTimedOut);
        Assert.Equal(0, finalSnapshot.ActiveSessions);
        Assert.Equal(1, finalSnapshot.PortPoolExhausted);
        Assert.Equal(1, finalSnapshot.UnknownSession);
        Assert.Equal(1, finalSnapshot.SessionSenderMismatch);
    }

    [Fact]
    public async Task HubRuntime_LogsFinalStatsAndClosesActiveSessionsOnShutdown()
    {
        var hubPort = ReserveUdpPort();
        var gamePort = ReserveUdpPort();
        var sessionPort = ReserveUdpPort();
        var config = new HubConfig(
            new UdpEndpoint(IPAddress.Loopback, hubPort),
            new UdpEndpoint(IPAddress.Loopback, gamePort),
            new SessionPortRange(sessionPort, sessionPort),
            SessionIdleTimeoutSeconds: 5,
            MaxSessions: 1,
            new RuntimeOptions(CompressThreshold: 32, CompressionLevel: 3, MaxPacketSize: 1200, StatsIntervalSeconds: 1, ReceiveQueueCapacity: 256, ReceiveWorkerCount: 1));
        var counters = new RuntimeCounters();
        var logs = new List<string>();
        using var edgeSocket = BindLoopbackSocket();
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runtime = new HubRuntime(config, counters, logs.Add);

        var runtimeTask = runtime.RunAsync(cancellationSource.Token);

        var registerBuffer = new byte[ProtocolConstants.HeaderSize];
        Assert.True(LaneZstdFrameCodec.TryWriteRegister(new RegisterFrame(SessionId.None), registerBuffer, out var registerBytesWritten));
        await edgeSocket.SendToAsync(registerBuffer.AsMemory(0, registerBytesWritten), SocketFlags.None, new IPEndPoint(IPAddress.Loopback, hubPort), cancellationSource.Token);

        var ackDatagram = await ReceiveAsync(edgeSocket, cancellationSource.Token);
        Assert.True(LaneZstdFrameCodec.TryRead(ackDatagram.Buffer, out var ackHeader, out _, out _));
        await WaitForAsync(() => counters.Snapshot(maxSessions: 1).ActiveSessions == 1, cancellationSource.Token);
        await WaitForAsync(() => logs.Any(line => line.Contains("hub stats ") && !line.Contains("final")), cancellationSource.Token);

        cancellationSource.Cancel();

        var closeDatagram = await ReceiveAsync(edgeSocket, CancellationToken.None);
        Assert.True(LaneZstdFrameCodec.TryRead(closeDatagram.Buffer, out var closeHeader, out _, out _));
        Assert.Equal(FrameType.Close, closeHeader.FrameType);
        Assert.Equal(ackHeader.SessionId, closeHeader.SessionId);

        await runtimeTask;

        Assert.Contains(logs, line => line.Contains("hub stats final"));

        var snapshot = counters.Snapshot(maxSessions: 1);
        Assert.Equal(1, snapshot.SessionsClosed);
        Assert.Equal(0, snapshot.ActiveSessions);
    }

    private static HubConfig CreateConfig(int hubPort, int gamePort, int sessionStartPort, int sessionEndPort, int idleTimeoutSeconds, int maxSessions)
    {
        return new HubConfig(
            new UdpEndpoint(IPAddress.Loopback, hubPort),
            new UdpEndpoint(IPAddress.Loopback, gamePort),
            new SessionPortRange(sessionStartPort, sessionEndPort),
            idleTimeoutSeconds,
            maxSessions,
            new RuntimeOptions(CompressThreshold: 32, CompressionLevel: 3, MaxPacketSize: 1200, StatsIntervalSeconds: 0, ReceiveQueueCapacity: 256, ReceiveWorkerCount: 1));
    }

    private static (int startPort, int endPort) ReserveUdpPortRange(int count)
    {
        for (var startPort = 40000; startPort <= 65000 - count; startPort++)
        {
            var sockets = new List<Socket>(count);

            try
            {
                for (var offset = 0; offset < count; offset++)
                {
                    sockets.Add(BindLoopbackSocket(startPort + offset));
                }

                return (startPort, startPort + count - 1);
            }
            catch (SocketException)
            {
            }
            finally
            {
                foreach (var socket in sockets)
                {
                    socket.Dispose();
                }
            }
        }

        throw new InvalidOperationException("Failed to reserve a UDP port range.");
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

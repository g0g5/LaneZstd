using System.Net;
using System.Net.Sockets;
using System.Text;

using LaneZstd.Core;

namespace LaneZstd.Tests;

public sealed class MultiEdgeIntegrationTests
{
    [Fact]
    public async Task MultiEdgeLoopback_RelaysFullDuplexAcrossDistinctSessions()
    {
        var hubPort = ReserveUdpPort();
        var gamePort = ReserveUdpPort();
        var edge1BindPort = ReserveUdpPort();
        var edge1GamePort = ReserveUdpPort();
        var edge2BindPort = ReserveUdpPort();
        var edge2GamePort = ReserveUdpPort();
        var sessionPortRange = ReserveUdpPortRange(2);

        var runtimeOptions = new RuntimeOptions(CompressThreshold: 32, CompressionLevel: 3, MaxPacketSize: 1200, StatsIntervalSeconds: 0, ReceiveQueueCapacity: 256, ReceiveWorkerCount: 1);
        var hubConfig = new HubConfig(
            new UdpEndpoint(IPAddress.Loopback, hubPort),
            new UdpEndpoint(IPAddress.Loopback, gamePort),
            new SessionPortRange(sessionPortRange.startPort, sessionPortRange.endPort),
            SessionIdleTimeoutSeconds: 5,
            MaxSessions: 2,
            runtimeOptions);
        var edge1Config = new EdgeConfig(
            new UdpEndpoint(IPAddress.Loopback, edge1BindPort),
            new UdpEndpoint(IPAddress.Loopback, hubPort),
            new UdpEndpoint(IPAddress.Loopback, edge1GamePort),
            runtimeOptions);
        var edge2Config = new EdgeConfig(
            new UdpEndpoint(IPAddress.Loopback, edge2BindPort),
            new UdpEndpoint(IPAddress.Loopback, hubPort),
            new UdpEndpoint(IPAddress.Loopback, edge2GamePort),
            runtimeOptions);

        var hubCounters = new RuntimeCounters();
        var edge1Counters = new RuntimeCounters();
        var edge2Counters = new RuntimeCounters();

        using var gameSocket = BindLoopbackSocket(gamePort);
        using var client1Socket = BindLoopbackSocket();
        using var client2Socket = BindLoopbackSocket();
        using var cancellationSource = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var hubRuntime = new HubRuntime(hubConfig, hubCounters);
        var edge1Runtime = new EdgeRuntime(edge1Config, edge1Counters);
        var edge2Runtime = new EdgeRuntime(edge2Config, edge2Counters);

        var hubTask = hubRuntime.RunAsync(cancellationSource.Token);
        var edge1Task = edge1Runtime.RunAsync(cancellationSource.Token);
        var edge2Task = edge2Runtime.RunAsync(cancellationSource.Token);

        await WaitForAsync(() => hubCounters.Snapshot(maxSessions: 2).ActiveSessions == 2, cancellationSource.Token);

        var edge1OutboundPayload = Encoding.ASCII.GetBytes("edge-1-raw");
        var edge2OutboundPayload = Encoding.ASCII.GetBytes(new string('Z', 256));

        await client1Socket.SendToAsync(edge1OutboundPayload, SocketFlags.None, new IPEndPoint(IPAddress.Loopback, edge1GamePort), cancellationSource.Token);
        await client2Socket.SendToAsync(edge2OutboundPayload, SocketFlags.None, new IPEndPoint(IPAddress.Loopback, edge2GamePort), cancellationSource.Token);

        var gameReceive1 = await ReceiveAsync(gameSocket, cancellationSource.Token);
        var gameReceive2 = await ReceiveAsync(gameSocket, cancellationSource.Token);

        var receivedByGame = new Dictionary<string, (byte[] Buffer, IPEndPoint RemoteEndPoint)>(StringComparer.Ordinal)
        {
            [Encoding.ASCII.GetString(gameReceive1.Buffer)] = (gameReceive1.Buffer, Assert.IsType<IPEndPoint>(gameReceive1.RemoteEndPoint)),
            [Encoding.ASCII.GetString(gameReceive2.Buffer)] = (gameReceive2.Buffer, Assert.IsType<IPEndPoint>(gameReceive2.RemoteEndPoint))
        };

        Assert.Equal(edge1OutboundPayload, receivedByGame[Encoding.ASCII.GetString(edge1OutboundPayload)].Buffer);
        Assert.Equal(edge2OutboundPayload, receivedByGame[Encoding.ASCII.GetString(edge2OutboundPayload)].Buffer);

        var edge1SessionEndPoint = receivedByGame[Encoding.ASCII.GetString(edge1OutboundPayload)].RemoteEndPoint;
        var edge2SessionEndPoint = receivedByGame[Encoding.ASCII.GetString(edge2OutboundPayload)].RemoteEndPoint;

        Assert.NotEqual(edge1SessionEndPoint.Port, edge2SessionEndPoint.Port);
        Assert.InRange(edge1SessionEndPoint.Port, sessionPortRange.startPort, sessionPortRange.endPort);
        Assert.InRange(edge2SessionEndPoint.Port, sessionPortRange.startPort, sessionPortRange.endPort);

        var edge1ReplyPayload = Encoding.ASCII.GetBytes("reply-to-edge-1");
        var edge2ReplyPayload = Encoding.ASCII.GetBytes(new string('Q', 192));

        await gameSocket.SendToAsync(edge1ReplyPayload, SocketFlags.None, edge1SessionEndPoint, cancellationSource.Token);
        await gameSocket.SendToAsync(edge2ReplyPayload, SocketFlags.None, edge2SessionEndPoint, cancellationSource.Token);

        var client1Receive = await ReceiveAsync(client1Socket, cancellationSource.Token);
        var client2Receive = await ReceiveAsync(client2Socket, cancellationSource.Token);

        Assert.Equal(edge1ReplyPayload, client1Receive.Buffer);
        Assert.Equal(edge2ReplyPayload, client2Receive.Buffer);

        cancellationSource.Cancel();
        await Task.WhenAll(hubTask, edge1Task, edge2Task);

        var hubSnapshot = hubCounters.Snapshot(maxSessions: 2);
        var edge1Snapshot = edge1Counters.Snapshot(maxSessions: 1);
        var edge2Snapshot = edge2Counters.Snapshot(maxSessions: 1);

        Assert.Equal(2, hubSnapshot.SessionsCreated);
        Assert.Equal(0, hubSnapshot.ActiveSessions);
        Assert.True(hubSnapshot.GamePacketsOut >= 2);
        Assert.True(hubSnapshot.GamePacketsIn >= 2);
        Assert.True(hubSnapshot.RawFramesOut >= 1);
        Assert.True(hubSnapshot.CompressedFramesOut >= 1);
        Assert.Equal(0, edge1Snapshot.UnknownSession);
        Assert.Equal(0, edge2Snapshot.UnknownSession);
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

    private static int ReserveUdpPort()
    {
        using var socket = BindLoopbackSocket();
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
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

    private static async Task WaitForAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(20, cancellationToken);
        }
    }
}

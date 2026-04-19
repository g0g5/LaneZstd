using System.Net;
using System.Net.Sockets;
using System.Threading;

using LaneZstd.Protocol;

namespace LaneZstd.Core;

public sealed class HubSession
{
    private long _lastActivityTicks;
    private int _isClosed;

    public HubSession(SessionId sessionId, IPEndPoint remoteEdgeEndPoint, int allocatedPort, Socket gameSocket)
    {
        SessionId = sessionId;
        RemoteEdgeEndPoint = remoteEdgeEndPoint;
        AllocatedPort = allocatedPort;
        GameSocket = gameSocket;
        Touch();
    }

    public SessionId SessionId { get; }

    public IPEndPoint RemoteEdgeEndPoint { get; }

    public int AllocatedPort { get; }

    public Socket GameSocket { get; }

    public bool IsClosed => Volatile.Read(ref _isClosed) != 0;

    public DateTime LastActivityUtc => new(Interlocked.Read(ref _lastActivityTicks), DateTimeKind.Utc);

    public void Touch() => Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);

    public bool TryMarkClosed() => Interlocked.Exchange(ref _isClosed, 1) == 0;
}

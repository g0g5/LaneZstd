using System.Net;
using System.Net.Sockets;

using LaneZstd.Protocol;

namespace LaneZstd.Core;

public sealed class HubSessionManager
{
    private readonly SessionPortPool _portPool;
    private readonly SessionIdGenerator _sessionIdGenerator = new();
    private readonly Dictionary<SessionId, HubSession> _sessionsById = [];
    private readonly Dictionary<string, HubSession> _sessionsByRemoteEndPoint = [];
    private readonly object _sync = new();

    public HubSessionManager(SessionPortRange portRange, int maxSessions)
    {
        _portPool = new SessionPortPool(portRange);
        MaxSessions = maxSessions;
    }

    public int MaxSessions { get; }

    public bool TryGetOrCreateSession(IPEndPoint remoteEdgeEndPoint, Func<int, Socket> bindSocket, out HubSession? session, out bool created, out bool isExhausted)
    {
        var remoteKey = GetRemoteKey(remoteEdgeEndPoint);

        lock (_sync)
        {
            if (_sessionsByRemoteEndPoint.TryGetValue(remoteKey, out session))
            {
                created = false;
                isExhausted = false;
                return true;
            }

            if (_sessionsById.Count >= MaxSessions || !_portPool.TryAcquire(out var allocatedPort))
            {
                session = null;
                created = false;
                isExhausted = true;
                return false;
            }

            try
            {
                var gameSocket = bindSocket(allocatedPort);
                HubSession createdSession;
                do
                {
                    createdSession = new HubSession(_sessionIdGenerator.Next(), remoteEdgeEndPoint, allocatedPort, gameSocket);
                }
                while (_sessionsById.ContainsKey(createdSession.SessionId));

                _sessionsById.Add(createdSession.SessionId, createdSession);
                _sessionsByRemoteEndPoint.Add(remoteKey, createdSession);
                session = createdSession;
                created = true;
                isExhausted = false;
                return true;
            }
            catch
            {
                _portPool.Release(allocatedPort);
                throw;
            }
        }
    }

    public bool TryGetOwnedSession(SessionId sessionId, IPEndPoint remoteEdgeEndPoint, out HubSession? session, out bool isSenderMismatch)
    {
        lock (_sync)
        {
            if (!_sessionsById.TryGetValue(sessionId, out session))
            {
                isSenderMismatch = false;
                return false;
            }

            isSenderMismatch = !session.RemoteEdgeEndPoint.Equals(remoteEdgeEndPoint);
            return !isSenderMismatch;
        }
    }

    public IReadOnlyList<HubSession> RemoveExpiredSessions(TimeSpan idleTimeout)
    {
        var thresholdUtc = DateTime.UtcNow - idleTimeout;
        List<HubSession> expiredSessions = [];

        lock (_sync)
        {
            foreach (var session in _sessionsById.Values)
            {
                if (!session.IsClosed && session.LastActivityUtc <= thresholdUtc)
                {
                    expiredSessions.Add(session);
                }
            }

            foreach (var session in expiredSessions)
            {
                RemoveSession_NoLock(session);
            }
        }

        return expiredSessions;
    }

    public bool TryRemoveOwnedSession(SessionId sessionId, IPEndPoint remoteEdgeEndPoint, out HubSession? session)
    {
        lock (_sync)
        {
            if (!_sessionsById.TryGetValue(sessionId, out session) || !session.RemoteEdgeEndPoint.Equals(remoteEdgeEndPoint))
            {
                session = null;
                return false;
            }

            RemoveSession_NoLock(session);
            return true;
        }
    }

    public IReadOnlyList<HubSession> RemoveAllSessions()
    {
        lock (_sync)
        {
            var sessions = _sessionsById.Values.ToArray();
            foreach (var session in sessions)
            {
                RemoveSession_NoLock(session);
            }

            return sessions;
        }
    }

    private void RemoveSession_NoLock(HubSession session)
    {
        if (!session.TryMarkClosed())
        {
            return;
        }

        _sessionsById.Remove(session.SessionId);
        _sessionsByRemoteEndPoint.Remove(GetRemoteKey(session.RemoteEdgeEndPoint));
        _portPool.Release(session.AllocatedPort);
        session.GameSocket.Dispose();
    }

    private static string GetRemoteKey(IPEndPoint remoteEdgeEndPoint) => remoteEdgeEndPoint.ToString();
}

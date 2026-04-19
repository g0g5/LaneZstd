using System.Threading;

namespace LaneZstd.Core;

public sealed class RuntimeCounters
{
    private long _activeSessions;
    private long _compressedFramesOut;
    private long _decompressError;
    private long _decodeElapsedTicks;
    private long _decodeOperations;
    private long _edgePacketsIn;
    private long _encodeElapsedTicks;
    private long _encodeOperations;
    private long _edgePacketsOut;
    private long _framedBytesOut;
    private long _gamePacketsIn;
    private long _gamePacketsOut;
    private long _hubPacketsIn;
    private long _hubPacketsOut;
    private long _oversizeDrop;
    private long _portPoolExhausted;
    private long _protocolError;
    private long _queueCompleted;
    private long _queueDequeued;
    private long _queueDropped;
    private long _queueEnqueued;
    private long _rawBytesIn;
    private long _rawFramesOut;
    private long _sessionSenderMismatch;
    private long _sessionsClosed;
    private long _sessionsCreated;
    private long _sessionsTimedOut;
    private long _unknownSession;

    public void Increment(RuntimeCounter counter, long amount = 1)
    {
        switch (counter)
        {
            case RuntimeCounter.SessionsCreated:
                Interlocked.Add(ref _sessionsCreated, amount);
                break;
            case RuntimeCounter.SessionsClosed:
                Interlocked.Add(ref _sessionsClosed, amount);
                break;
            case RuntimeCounter.SessionsTimedOut:
                Interlocked.Add(ref _sessionsTimedOut, amount);
                break;
            case RuntimeCounter.EdgePacketsIn:
                Interlocked.Add(ref _edgePacketsIn, amount);
                break;
            case RuntimeCounter.EdgePacketsOut:
                Interlocked.Add(ref _edgePacketsOut, amount);
                break;
            case RuntimeCounter.HubPacketsIn:
                Interlocked.Add(ref _hubPacketsIn, amount);
                break;
            case RuntimeCounter.HubPacketsOut:
                Interlocked.Add(ref _hubPacketsOut, amount);
                break;
            case RuntimeCounter.GamePacketsIn:
                Interlocked.Add(ref _gamePacketsIn, amount);
                break;
            case RuntimeCounter.GamePacketsOut:
                Interlocked.Add(ref _gamePacketsOut, amount);
                break;
            case RuntimeCounter.RawFramesOut:
                Interlocked.Add(ref _rawFramesOut, amount);
                break;
            case RuntimeCounter.CompressedFramesOut:
                Interlocked.Add(ref _compressedFramesOut, amount);
                break;
            case RuntimeCounter.RawBytesIn:
                Interlocked.Add(ref _rawBytesIn, amount);
                break;
            case RuntimeCounter.FramedBytesOut:
                Interlocked.Add(ref _framedBytesOut, amount);
                break;
            case RuntimeCounter.OversizeDrop:
                Interlocked.Add(ref _oversizeDrop, amount);
                break;
            case RuntimeCounter.ProtocolError:
                Interlocked.Add(ref _protocolError, amount);
                break;
            case RuntimeCounter.DecompressError:
                Interlocked.Add(ref _decompressError, amount);
                break;
            case RuntimeCounter.EncodeOperations:
                Interlocked.Add(ref _encodeOperations, amount);
                break;
            case RuntimeCounter.EncodeElapsedTicks:
                Interlocked.Add(ref _encodeElapsedTicks, amount);
                break;
            case RuntimeCounter.DecodeOperations:
                Interlocked.Add(ref _decodeOperations, amount);
                break;
            case RuntimeCounter.DecodeElapsedTicks:
                Interlocked.Add(ref _decodeElapsedTicks, amount);
                break;
            case RuntimeCounter.QueueEnqueued:
                Interlocked.Add(ref _queueEnqueued, amount);
                break;
            case RuntimeCounter.QueueDequeued:
                Interlocked.Add(ref _queueDequeued, amount);
                break;
            case RuntimeCounter.QueueDropped:
                Interlocked.Add(ref _queueDropped, amount);
                break;
            case RuntimeCounter.QueueCompleted:
                Interlocked.Add(ref _queueCompleted, amount);
                break;
            case RuntimeCounter.UnknownSession:
                Interlocked.Add(ref _unknownSession, amount);
                break;
            case RuntimeCounter.SessionSenderMismatch:
                Interlocked.Add(ref _sessionSenderMismatch, amount);
                break;
            case RuntimeCounter.PortPoolExhausted:
                Interlocked.Add(ref _portPoolExhausted, amount);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(counter), counter, null);
        }
    }

    public long ChangeActiveSessions(int delta) => Interlocked.Add(ref _activeSessions, delta);

    public RuntimeStatsSnapshot Snapshot(int maxSessions) => new(
        SessionsCreated: Interlocked.Read(ref _sessionsCreated),
        SessionsClosed: Interlocked.Read(ref _sessionsClosed),
        SessionsTimedOut: Interlocked.Read(ref _sessionsTimedOut),
        ActiveSessions: Interlocked.Read(ref _activeSessions),
        EdgePacketsIn: Interlocked.Read(ref _edgePacketsIn),
        EdgePacketsOut: Interlocked.Read(ref _edgePacketsOut),
        HubPacketsIn: Interlocked.Read(ref _hubPacketsIn),
        HubPacketsOut: Interlocked.Read(ref _hubPacketsOut),
        GamePacketsIn: Interlocked.Read(ref _gamePacketsIn),
        GamePacketsOut: Interlocked.Read(ref _gamePacketsOut),
        RawFramesOut: Interlocked.Read(ref _rawFramesOut),
        CompressedFramesOut: Interlocked.Read(ref _compressedFramesOut),
        RawBytesIn: Interlocked.Read(ref _rawBytesIn),
        FramedBytesOut: Interlocked.Read(ref _framedBytesOut),
        OversizeDrop: Interlocked.Read(ref _oversizeDrop),
        ProtocolError: Interlocked.Read(ref _protocolError),
        DecompressError: Interlocked.Read(ref _decompressError),
        EncodeOperations: Interlocked.Read(ref _encodeOperations),
        EncodeElapsedTicks: Interlocked.Read(ref _encodeElapsedTicks),
        DecodeOperations: Interlocked.Read(ref _decodeOperations),
        DecodeElapsedTicks: Interlocked.Read(ref _decodeElapsedTicks),
        QueueEnqueued: Interlocked.Read(ref _queueEnqueued),
        QueueDequeued: Interlocked.Read(ref _queueDequeued),
        QueueDropped: Interlocked.Read(ref _queueDropped),
        QueueCompleted: Interlocked.Read(ref _queueCompleted),
        UnknownSession: Interlocked.Read(ref _unknownSession),
        SessionSenderMismatch: Interlocked.Read(ref _sessionSenderMismatch),
        PortPoolExhausted: Interlocked.Read(ref _portPoolExhausted),
        MaxSessions: maxSessions);
}

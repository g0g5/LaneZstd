using System.Threading;

namespace LaneZstd.Core;

public sealed class RuntimeCounters
{
    private long _activeSessions;
    private long _compressedFramesOut;
    private long _decompressError;
    private long _edgePacketsIn;
    private long _edgePacketsOut;
    private long _framedBytesOut;
    private long _gamePacketsIn;
    private long _gamePacketsOut;
    private long _hubPacketsIn;
    private long _hubPacketsOut;
    private long _oversizeDrop;
    private long _portPoolExhausted;
    private long _protocolError;
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
        UnknownSession: Interlocked.Read(ref _unknownSession),
        SessionSenderMismatch: Interlocked.Read(ref _sessionSenderMismatch),
        PortPoolExhausted: Interlocked.Read(ref _portPoolExhausted),
        MaxSessions: maxSessions);
}

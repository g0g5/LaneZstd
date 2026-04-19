namespace LaneZstd.Core;

public enum RuntimeCounter
{
    SessionsCreated,
    SessionsClosed,
    SessionsTimedOut,
    EdgePacketsIn,
    EdgePacketsOut,
    HubPacketsIn,
    HubPacketsOut,
    GamePacketsIn,
    GamePacketsOut,
    RawFramesOut,
    CompressedFramesOut,
    RawBytesIn,
    FramedBytesOut,
    OversizeDrop,
    ProtocolError,
    DecompressError,
    UnknownSession,
    SessionSenderMismatch,
    PortPoolExhausted,
}

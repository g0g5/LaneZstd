namespace LaneZstd.Protocol;

public readonly record struct RegisterFrame(SessionId SessionId);

public readonly record struct RegisterAckFrame(SessionId SessionId, ushort AllocatedPort);

public readonly record struct DataFrame(SessionId SessionId, ushort RawLength, bool IsCompressed);

public readonly record struct CloseFrame(SessionId SessionId);

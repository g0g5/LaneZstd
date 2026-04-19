namespace LaneZstd.Core;

public sealed record HubConfig(
    UdpEndpoint BindEndpoint,
    UdpEndpoint GameEndpoint,
    SessionPortRange SessionPortRange,
    int SessionIdleTimeoutSeconds,
    int MaxSessions,
    RuntimeOptions Runtime);

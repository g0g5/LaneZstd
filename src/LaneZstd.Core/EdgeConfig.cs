namespace LaneZstd.Core;

public sealed record EdgeConfig(
    UdpEndpoint BindEndpoint,
    UdpEndpoint HubEndpoint,
    UdpEndpoint GameListenEndpoint,
    RuntimeOptions Runtime);

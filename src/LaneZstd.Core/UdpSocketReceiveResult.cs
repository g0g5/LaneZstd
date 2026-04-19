using System.Net;

namespace LaneZstd.Core;

public readonly record struct UdpSocketReceiveResult(int ReceivedBytes, EndPoint RemoteEndPoint);

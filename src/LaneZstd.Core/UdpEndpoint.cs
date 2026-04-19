using System.Globalization;
using System.Net;

namespace LaneZstd.Core;

public readonly record struct UdpEndpoint(IPAddress Address, int Port)
{
    public bool IsWildcard => Address.Equals(IPAddress.Any) || Address.Equals(IPAddress.IPv6Any);

    public override string ToString() => $"{Address}:{Port.ToString(CultureInfo.InvariantCulture)}";

    public static bool TryParse(string? value, out UdpEndpoint endpoint)
    {
        endpoint = default;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var separatorIndex = value.LastIndexOf(':');
        if (separatorIndex <= 0 || separatorIndex == value.Length - 1)
        {
            return false;
        }

        var addressText = value[..separatorIndex];
        var portText = value[(separatorIndex + 1)..];

        if (!IPAddress.TryParse(addressText, out var address))
        {
            return false;
        }

        if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port))
        {
            return false;
        }

        if (port is < 1 or > 65535)
        {
            return false;
        }

        endpoint = new UdpEndpoint(address, port);
        return true;
    }
}

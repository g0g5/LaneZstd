using System.Globalization;

namespace LaneZstd.Core;

public readonly record struct SessionPortRange(int StartPort, int EndPort)
{
    public int Count => (EndPort - StartPort) + 1;

    public bool Contains(int port) => port >= StartPort && port <= EndPort;

    public override string ToString() => $"{StartPort.ToString(CultureInfo.InvariantCulture)}-{EndPort.ToString(CultureInfo.InvariantCulture)}";

    public static bool TryParse(string? value, out SessionPortRange portRange)
    {
        portRange = default;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var separatorIndex = value.IndexOf('-');
        if (separatorIndex <= 0 || separatorIndex == value.Length - 1)
        {
            return false;
        }

        var startText = value[..separatorIndex];
        var endText = value[(separatorIndex + 1)..];

        if (!int.TryParse(startText, NumberStyles.None, CultureInfo.InvariantCulture, out var startPort) ||
            !int.TryParse(endText, NumberStyles.None, CultureInfo.InvariantCulture, out var endPort))
        {
            return false;
        }

        if (startPort is < 1 or > 65535 || endPort is < 1 or > 65535)
        {
            return false;
        }

        if (startPort > endPort)
        {
            return false;
        }

        portRange = new SessionPortRange(startPort, endPort);
        return true;
    }
}

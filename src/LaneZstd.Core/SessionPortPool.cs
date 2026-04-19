namespace LaneZstd.Core;

public sealed class SessionPortPool
{
    private readonly Queue<int> _availablePorts;
    private readonly HashSet<int> _leasedPorts = [];
    private readonly object _sync = new();

    public SessionPortPool(SessionPortRange range)
    {
        Range = range;
        _availablePorts = new Queue<int>(range.Count);

        for (var port = range.StartPort; port <= range.EndPort; port++)
        {
            _availablePorts.Enqueue(port);
        }
    }

    public SessionPortRange Range { get; }

    public int Capacity => Range.Count;

    public int AvailableCount
    {
        get
        {
            lock (_sync)
            {
                return _availablePorts.Count;
            }
        }
    }

    public bool TryAcquire(out int port)
    {
        lock (_sync)
        {
            if (_availablePorts.Count == 0)
            {
                port = 0;
                return false;
            }

            port = _availablePorts.Dequeue();
            _leasedPorts.Add(port);
            return true;
        }
    }

    public bool Release(int port)
    {
        lock (_sync)
        {
            if (!_leasedPorts.Remove(port))
            {
                return false;
            }

            _availablePorts.Enqueue(port);
            return true;
        }
    }
}

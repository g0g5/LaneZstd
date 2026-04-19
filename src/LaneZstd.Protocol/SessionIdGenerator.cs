using System.Threading;

namespace LaneZstd.Protocol;

public sealed class SessionIdGenerator
{
    private int _next = Random.Shared.Next(1, int.MaxValue);

    public SessionId Next()
    {
        while (true)
        {
            var value = unchecked((uint)Interlocked.Increment(ref _next));
            if (value != 0)
            {
                return new SessionId(value);
            }
        }
    }
}

namespace LaneZstd.Protocol;

public readonly record struct SessionId(uint Value)
{
    public static SessionId None => new(0);

    public bool IsEmpty => Value == 0;

    public override string ToString() => Value.ToString();
}

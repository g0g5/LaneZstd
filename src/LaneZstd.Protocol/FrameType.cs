namespace LaneZstd.Protocol;

public enum FrameType : byte
{
    Register = 0x01,
    RegisterAck = 0x02,
    Data = 0x03,
    Close = 0x04,
}

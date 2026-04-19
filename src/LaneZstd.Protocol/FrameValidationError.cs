namespace LaneZstd.Protocol;

public enum FrameValidationError
{
    None = 0,
    DestinationTooSmall,
    DatagramTooSmall,
    InvalidMagic,
    UnsupportedVersion,
    UnknownFrameType,
    InvalidFlags,
    ReservedMustBeZero,
    BodyLengthMismatch,
    RegisterSessionMustBeZero,
    NonRegisterSessionMustBeNonZero,
    RawDataLengthMismatch,
    CompressedRawLengthMustBeNonZero,
    RegisterBodyMustBeEmpty,
    RegisterAckBodyMustBeTwoBytes,
    CloseBodyMustBeEmpty,
    DecompressedLengthMismatch,
}

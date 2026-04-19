using System.Buffers.Binary;

namespace LaneZstd.Protocol;

public static class LaneZstdFrameCodec
{
    public static bool TryWriteRegister(RegisterFrame frame, Span<byte> destination, out int bytesWritten)
        => TryWrite(FrameType.Register, FrameFlags.None, frame.SessionId, 0, ReadOnlySpan<byte>.Empty, destination, out bytesWritten);

    public static bool TryWriteRegisterAck(RegisterAckFrame frame, Span<byte> destination, out int bytesWritten)
    {
        Span<byte> body = stackalloc byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16LittleEndian(body, frame.AllocatedPort);
        return TryWrite(FrameType.RegisterAck, FrameFlags.None, frame.SessionId, 0, body, destination, out bytesWritten);
    }

    public static bool TryWriteData(DataFrame frame, ReadOnlySpan<byte> body, Span<byte> destination, out int bytesWritten)
    {
        var flags = frame.IsCompressed ? FrameFlags.Compressed : FrameFlags.None;
        return TryWrite(FrameType.Data, flags, frame.SessionId, frame.RawLength, body, destination, out bytesWritten);
    }

    public static bool TryWriteClose(CloseFrame frame, Span<byte> destination, out int bytesWritten)
        => TryWrite(FrameType.Close, FrameFlags.None, frame.SessionId, 0, ReadOnlySpan<byte>.Empty, destination, out bytesWritten);

    public static bool TryRead(
        ReadOnlySpan<byte> datagram,
        out LaneZstdFrameHeader header,
        out ReadOnlySpan<byte> body,
        out FrameValidationError error)
    {
        header = default;
        body = default;

        if (datagram.Length < ProtocolConstants.HeaderSize)
        {
            error = FrameValidationError.DatagramTooSmall;
            return false;
        }

        header = LaneZstdFrameHeader.Read(datagram[..ProtocolConstants.HeaderSize]);
        body = datagram[ProtocolConstants.HeaderSize..];

        error = Validate(header, body.Length);
        return error == FrameValidationError.None;
    }

    public static FrameValidationError ValidateDecodedDataLength(LaneZstdFrameHeader header, int decodedLength)
    {
        if (header.FrameType != FrameType.Data || !header.IsCompressed)
        {
            return FrameValidationError.None;
        }

        return decodedLength == header.RawLength
            ? FrameValidationError.None
            : FrameValidationError.DecompressedLengthMismatch;
    }

    private static bool TryWrite(
        FrameType frameType,
        FrameFlags flags,
        SessionId sessionId,
        ushort rawLength,
        ReadOnlySpan<byte> body,
        Span<byte> destination,
        out int bytesWritten)
    {
        bytesWritten = 0;

        if (destination.Length < ProtocolConstants.HeaderSize + body.Length || body.Length > ushort.MaxValue)
        {
            return false;
        }

        ProtocolConstants.WriteHeader(destination, frameType, flags, sessionId, rawLength, (ushort)body.Length);
        body.CopyTo(destination[ProtocolConstants.HeaderSize..]);
        bytesWritten = ProtocolConstants.HeaderSize + body.Length;
        return true;
    }

    private static FrameValidationError Validate(LaneZstdFrameHeader header, int bodyLength)
    {
        if (header.Magic != ProtocolConstants.Magic)
        {
            return FrameValidationError.InvalidMagic;
        }

        if (header.Version != ProtocolConstants.Version)
        {
            return FrameValidationError.UnsupportedVersion;
        }

        if (!Enum.IsDefined(header.FrameType))
        {
            return FrameValidationError.UnknownFrameType;
        }

        if (header.Flags is not FrameFlags.None and not FrameFlags.Compressed)
        {
            return FrameValidationError.InvalidFlags;
        }

        if (header.Reserved != 0)
        {
            return FrameValidationError.ReservedMustBeZero;
        }

        if (header.BodyLength != bodyLength)
        {
            return FrameValidationError.BodyLengthMismatch;
        }

        if (header.FrameType == FrameType.Register)
        {
            if (!header.SessionId.IsEmpty)
            {
                return FrameValidationError.RegisterSessionMustBeZero;
            }

            if (header.BodyLength != 0)
            {
                return FrameValidationError.RegisterBodyMustBeEmpty;
            }

            return FrameValidationError.None;
        }

        if (header.SessionId.IsEmpty)
        {
            return FrameValidationError.NonRegisterSessionMustBeNonZero;
        }

        return header.FrameType switch
        {
            FrameType.RegisterAck when header.BodyLength != sizeof(ushort) => FrameValidationError.RegisterAckBodyMustBeTwoBytes,
            FrameType.Close when header.BodyLength != 0 => FrameValidationError.CloseBodyMustBeEmpty,
            FrameType.Data when header.IsCompressed && header.RawLength == 0 => FrameValidationError.CompressedRawLengthMustBeNonZero,
            FrameType.Data when !header.IsCompressed && header.BodyLength != header.RawLength => FrameValidationError.RawDataLengthMismatch,
            _ => FrameValidationError.None,
        };
    }
}

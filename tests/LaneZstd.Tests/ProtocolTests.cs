using LaneZstd.Cli;
using LaneZstd.Core;
using LaneZstd.Protocol;

namespace LaneZstd.Tests;

public class ProtocolTests
{
    [Fact]
    public void Phase1_Scaffold_WiresAllRuntimeLayers()
    {
        _ = typeof(CoreAssemblyMarker);
        _ = typeof(ProtocolAssemblyMarker);

        var command = CliApplication.BuildRootCommand();

        Assert.Equal("LaneZstd UDP hub runtime", command.Description);
    }

    [Fact]
    public void TryWriteAndReadRegister_UsesSessionZeroAndEmptyBody()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.HeaderSize];

        var written = LaneZstdFrameCodec.TryWriteRegister(new RegisterFrame(SessionId.None), buffer, out var bytesWritten);
        var read = LaneZstdFrameCodec.TryRead(buffer[..bytesWritten], out var header, out var body, out var error);

        Assert.True(written);
        Assert.True(read);
        Assert.Equal(FrameValidationError.None, error);
        Assert.Equal(FrameType.Register, header.FrameType);
        Assert.True(header.SessionId.IsEmpty);
        Assert.Empty(body.ToArray());
    }

    [Fact]
    public void TryWriteAndReadRegisterAck_PreservesAssignedPort()
    {
        var sessionId = new SessionId(42);
        Span<byte> buffer = stackalloc byte[ProtocolConstants.HeaderSize + sizeof(ushort)];

        var written = LaneZstdFrameCodec.TryWriteRegisterAck(new RegisterAckFrame(sessionId, 40001), buffer, out var bytesWritten);
        var read = LaneZstdFrameCodec.TryRead(buffer[..bytesWritten], out var header, out var body, out var error);

        Assert.True(written);
        Assert.True(read);
        Assert.Equal(FrameValidationError.None, error);
        Assert.Equal(FrameType.RegisterAck, header.FrameType);
        Assert.Equal(sessionId, header.SessionId);
        Assert.Equal((ushort)40001, BitConverter.ToUInt16(body));
    }

    [Fact]
    public void TryWriteAndReadDataFrame_SupportsRawAndCompressedFlags()
    {
        var sessionId = new SessionId(7);
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        Span<byte> buffer = stackalloc byte[ProtocolConstants.HeaderSize + payload.Length];

        var written = LaneZstdFrameCodec.TryWriteData(new DataFrame(sessionId, (ushort)payload.Length, false), payload, buffer, out var bytesWritten);
        var read = LaneZstdFrameCodec.TryRead(buffer[..bytesWritten], out var rawHeader, out var rawBody, out var rawError);

        Assert.True(written);
        Assert.True(read);
        Assert.Equal(FrameValidationError.None, rawError);
        Assert.False(rawHeader.IsCompressed);
        Assert.Equal(payload, rawBody.ToArray());

        written = LaneZstdFrameCodec.TryWriteData(new DataFrame(sessionId, 11, true), payload, buffer, out bytesWritten);
        read = LaneZstdFrameCodec.TryRead(buffer[..bytesWritten], out var compressedHeader, out var compressedBody, out var compressedError);

        Assert.True(written);
        Assert.True(read);
        Assert.Equal(FrameValidationError.None, compressedError);
        Assert.True(compressedHeader.IsCompressed);
        Assert.Equal((ushort)11, compressedHeader.RawLength);
        Assert.Equal(payload, compressedBody.ToArray());
    }

    [Fact]
    public void TryRead_RejectsInvalidHeaderFields()
    {
        var datagram = BuildDatagram(
            magic: 0x1234,
            version: ProtocolConstants.Version,
            frameType: FrameType.Register,
            flags: FrameFlags.None,
            sessionId: SessionId.None,
            rawLength: 0,
            body: []);

        Assert.False(LaneZstdFrameCodec.TryRead(datagram, out _, out _, out var magicError));
        Assert.Equal(FrameValidationError.InvalidMagic, magicError);

        datagram = BuildDatagram(
            magic: ProtocolConstants.Magic,
            version: 9,
            frameType: FrameType.Register,
            flags: FrameFlags.None,
            sessionId: SessionId.None,
            rawLength: 0,
            body: []);

        Assert.False(LaneZstdFrameCodec.TryRead(datagram, out _, out _, out var versionError));
        Assert.Equal(FrameValidationError.UnsupportedVersion, versionError);

        datagram = BuildDatagram(
            magic: ProtocolConstants.Magic,
            version: ProtocolConstants.Version,
            frameType: FrameType.Register,
            flags: FrameFlags.None,
            sessionId: SessionId.None,
            rawLength: 0,
            body: [],
            reserved: 1);

        Assert.False(LaneZstdFrameCodec.TryRead(datagram, out _, out _, out var reservedError));
        Assert.Equal(FrameValidationError.ReservedMustBeZero, reservedError);
    }

    [Fact]
    public void TryRead_RejectsBodyAndSessionRuleViolations()
    {
        var bodyMismatch = BuildDatagram(
            magic: ProtocolConstants.Magic,
            version: ProtocolConstants.Version,
            frameType: FrameType.Data,
            flags: FrameFlags.None,
            sessionId: new SessionId(1),
            rawLength: 3,
            body: [1, 2],
            declaredBodyLength: 3);

        Assert.False(LaneZstdFrameCodec.TryRead(bodyMismatch, out _, out _, out var bodyError));
        Assert.Equal(FrameValidationError.BodyLengthMismatch, bodyError);

        var registerWithSession = BuildDatagram(
            magic: ProtocolConstants.Magic,
            version: ProtocolConstants.Version,
            frameType: FrameType.Register,
            flags: FrameFlags.None,
            sessionId: new SessionId(9),
            rawLength: 0,
            body: []);

        Assert.False(LaneZstdFrameCodec.TryRead(registerWithSession, out _, out _, out var registerError));
        Assert.Equal(FrameValidationError.RegisterSessionMustBeZero, registerError);

        var dataWithoutSession = BuildDatagram(
            magic: ProtocolConstants.Magic,
            version: ProtocolConstants.Version,
            frameType: FrameType.Data,
            flags: FrameFlags.None,
            sessionId: SessionId.None,
            rawLength: 1,
            body: [1]);

        Assert.False(LaneZstdFrameCodec.TryRead(dataWithoutSession, out _, out _, out var sessionError));
        Assert.Equal(FrameValidationError.NonRegisterSessionMustBeNonZero, sessionError);
    }

    [Fact]
    public void TryRead_RejectsRawAndCompressedDataLengthViolations()
    {
        var rawMismatch = BuildDatagram(
            magic: ProtocolConstants.Magic,
            version: ProtocolConstants.Version,
            frameType: FrameType.Data,
            flags: FrameFlags.None,
            sessionId: new SessionId(2),
            rawLength: 4,
            body: [1, 2, 3]);

        Assert.False(LaneZstdFrameCodec.TryRead(rawMismatch, out _, out _, out var rawError));
        Assert.Equal(FrameValidationError.RawDataLengthMismatch, rawError);

        var compressedWithoutRawLength = BuildDatagram(
            magic: ProtocolConstants.Magic,
            version: ProtocolConstants.Version,
            frameType: FrameType.Data,
            flags: FrameFlags.Compressed,
            sessionId: new SessionId(2),
            rawLength: 0,
            body: [1]);

        Assert.False(LaneZstdFrameCodec.TryRead(compressedWithoutRawLength, out _, out _, out var compressedError));
        Assert.Equal(FrameValidationError.CompressedRawLengthMustBeNonZero, compressedError);
    }

    [Fact]
    public void ValidateDecodedDataLength_RejectsUnexpectedOutputLength()
    {
        var datagram = BuildDatagram(
            magic: ProtocolConstants.Magic,
            version: ProtocolConstants.Version,
            frameType: FrameType.Data,
            flags: FrameFlags.Compressed,
            sessionId: new SessionId(55),
            rawLength: 12,
            body: [9, 9, 9]);

        var read = LaneZstdFrameCodec.TryRead(datagram, out var header, out _, out var error);

        Assert.True(read);
        Assert.Equal(FrameValidationError.None, error);
        Assert.Equal(FrameValidationError.DecompressedLengthMismatch, LaneZstdFrameCodec.ValidateDecodedDataLength(header, 11));
        Assert.Equal(FrameValidationError.None, LaneZstdFrameCodec.ValidateDecodedDataLength(header, 12));
    }

    [Fact]
    public void SessionIdGenerator_NeverReturnsZero()
    {
        var generator = new SessionIdGenerator();

        for (var i = 0; i < 32; i++)
        {
            Assert.False(generator.Next().IsEmpty);
        }
    }

    private static byte[] BuildDatagram(
        ushort magic,
        byte version,
        FrameType frameType,
        FrameFlags flags,
        SessionId sessionId,
        ushort rawLength,
        byte[] body,
        ushort? declaredBodyLength = null,
        byte reserved = 0)
    {
        var datagram = new byte[ProtocolConstants.HeaderSize + body.Length];
        ProtocolConstants.WriteHeader(datagram, frameType, flags, sessionId, rawLength, declaredBodyLength ?? (ushort)body.Length);
        datagram[0] = (byte)(magic & 0xFF);
        datagram[1] = (byte)(magic >> 8);
        datagram[2] = version;
        datagram[5] = reserved;
        body.CopyTo(datagram.AsSpan(ProtocolConstants.HeaderSize));
        return datagram;
    }
}

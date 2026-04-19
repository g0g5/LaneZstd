using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using LaneZstd.Core;

namespace LaneZstd.Cli;

public sealed record BenchConfig(
    TimeSpan Duration,
    TimeSpan Warmup,
    int MessagesPerSecond,
    int AveragePayloadBytes,
    int MinPayloadBytes,
    int MaxPayloadBytes,
    int Seed,
    string OutputFormat,
    RuntimeOptions Runtime);

public sealed record BenchDirectionMetrics(
    string Direction,
    long MessagesSent,
    long MessagesReceived,
    long RawBytes,
    long FramedBytes,
    long RawFrames,
    long CompressedFrames,
    double RawBytesPerSecond,
    double FramedBytesPerSecond,
    double CompressionRatio,
    double CompressionSavings,
    long Lost,
    long Duplicated,
    long Corrupted,
    BenchCorruptionMetrics Corruption);

public sealed record BenchCorruptionMetrics(
    long JsonInvalid,
    long DirectionMismatch,
    long ChecksumMismatch);

public sealed record BenchResult(
    bool IsSuccessful,
    BenchConfig Config,
    TimeSpan MeasurementWindow,
    BenchDirectionMetrics EdgeToHub,
    BenchDirectionMetrics HubToEdge)
{
    public string FormatSummary(string outputFormat)
    {
        if (string.Equals(outputFormat, "json", StringComparison.OrdinalIgnoreCase))
        {
            return JsonSerializer.Serialize(this, TrafficPayloadJsonContext.Default.BenchResult);
        }

        return string.Join(
            Environment.NewLine,
            $"bench duration={MeasurementWindow.TotalSeconds:F2}s warmup={Config.Warmup.TotalSeconds:F2}s rate_per_direction={Config.MessagesPerSecond}/s payload_avg={Config.AveragePayloadBytes}B payload_range={Config.MinPayloadBytes}-{Config.MaxPayloadBytes}B seed={Config.Seed} threshold={Config.Runtime.CompressThreshold} level={Config.Runtime.CompressionLevel} max_packet={Config.Runtime.MaxPacketSize}",
            FormatDirection(EdgeToHub),
            FormatDirection(HubToEdge),
            $"integrity sent={EdgeToHub.MessagesSent + HubToEdge.MessagesSent} received={EdgeToHub.MessagesReceived + HubToEdge.MessagesReceived} lost={EdgeToHub.Lost + HubToEdge.Lost} duplicated={EdgeToHub.Duplicated + HubToEdge.Duplicated} corrupted={EdgeToHub.Corrupted + HubToEdge.Corrupted}");
    }

    private static string FormatDirection(BenchDirectionMetrics metrics) =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{metrics.Direction} messages_sent={metrics.MessagesSent} messages_received={metrics.MessagesReceived} raw_bytes={metrics.RawBytes} framed_bytes={metrics.FramedBytes} raw_Bps={metrics.RawBytesPerSecond:F2} framed_Bps={metrics.FramedBytesPerSecond:F2} ratio={metrics.CompressionRatio:F2} savings={metrics.CompressionSavings:F2} raw_frames={metrics.RawFrames} compressed_frames={metrics.CompressedFrames} lost={metrics.Lost} duplicated={metrics.Duplicated} corrupted={metrics.Corrupted} corrupted_json={metrics.Corruption.JsonInvalid} corrupted_direction={metrics.Corruption.DirectionMismatch} corrupted_checksum={metrics.Corruption.ChecksumMismatch}");
}

public static class TrafficBenchRunner
{
    private const int SessionPortCount = CliDefaults.BenchSessionPortCount;
    private static readonly TimeSpan DrainIdleThreshold = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(2);

    public static async Task<BenchResult> RunAsync(BenchConfig config, Action<string>? log = null, CancellationToken cancellationToken = default)
    {
        var hubPort = ReserveUdpPort();
        var gamePort = ReserveUdpPort();
        var edgeBindPort = ReserveUdpPort();
        var edgeGamePort = ReserveUdpPort();
        var sessionPortRange = ReserveUdpPortRange(SessionPortCount);

        var hubConfig = new HubConfig(
            new UdpEndpoint(IPAddress.Loopback, hubPort),
            new UdpEndpoint(IPAddress.Loopback, gamePort),
            new SessionPortRange(sessionPortRange.startPort, sessionPortRange.endPort),
            SessionIdleTimeoutSeconds: 10,
            MaxSessions: 1,
            config.Runtime);
        var edgeConfig = new EdgeConfig(
            new UdpEndpoint(IPAddress.Loopback, edgeBindPort),
            new UdpEndpoint(IPAddress.Loopback, hubPort),
            new UdpEndpoint(IPAddress.Loopback, edgeGamePort),
            config.Runtime);

        var hubCounters = new RuntimeCounters();
        var edgeCounters = new RuntimeCounters();
        using var gameSocket = BindLoopbackSocket(gamePort);
        using var clientSocket = BindLoopbackSocket();
        using var runCancellationSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var hubRuntime = new HubRuntime(hubConfig, hubCounters, log);
        var edgeRuntime = new EdgeRuntime(edgeConfig, edgeCounters, log);

        log?.Invoke($"bench starting local loopback topology hub=127.0.0.1:{hubPort} edge=127.0.0.1:{edgeBindPort} edge_game=127.0.0.1:{edgeGamePort} game=127.0.0.1:{gamePort} session_ports={sessionPortRange.startPort}-{sessionPortRange.endPort}");

        var hubTask = hubRuntime.RunAsync(runCancellationSource.Token);
        var edgeTask = edgeRuntime.RunAsync(runCancellationSource.Token);

        try
        {
            await WaitForAsync(() => hubCounters.Snapshot(1).ActiveSessions == 1 && edgeCounters.Snapshot(1).ActiveSessions == 1, runCancellationSource.Token);

            var sessionEndPoint = await EstablishSessionAsync(clientSocket, gameSocket, edgeGamePort, config.Seed, runCancellationSource.Token);
            var edgeToHubTracker = new TrafficDirectionTracker();
            var hubToEdgeTracker = new TrafficDirectionTracker();

            using var receiverCancellationSource = CancellationTokenSource.CreateLinkedTokenSource(runCancellationSource.Token);
            var gameReceiveTask = ReceiveLoopAsync(gameSocket, "edge->hub", edgeToHubTracker, receiverCancellationSource.Token);
            var clientReceiveTask = ReceiveLoopAsync(clientSocket, "hub->edge", hubToEdgeTracker, receiverCancellationSource.Token);

            var stopwatch = Stopwatch.StartNew();
            var totalDuration = config.Warmup + config.Duration;
            var edgeSenderTask = SendLoopAsync(clientSocket, new IPEndPoint(IPAddress.Loopback, edgeGamePort), "edge->hub", edgeToHubTracker, config, stopwatch, totalDuration, config.Seed ^ 0x13579BDF, runCancellationSource.Token);
            var hubSenderTask = SendLoopAsync(gameSocket, sessionEndPoint, "hub->edge", hubToEdgeTracker, config, stopwatch, totalDuration, config.Seed ^ 0x2468ACE0, runCancellationSource.Token);

            if (config.Warmup > TimeSpan.Zero)
            {
                await Task.Delay(config.Warmup, runCancellationSource.Token);
            }

            edgeToHubTracker.BeginMeasurement();
            hubToEdgeTracker.BeginMeasurement();
            var baselineCapturedAt = stopwatch.Elapsed;
            var edgeBaseline = edgeCounters.Snapshot(1);
            var hubBaseline = hubCounters.Snapshot(1);
            log?.Invoke($"bench warmup complete after {baselineCapturedAt.TotalSeconds:F2}s; collecting stats for {config.Duration.TotalSeconds:F2}s");

            await Task.WhenAll(edgeSenderTask, hubSenderTask);
            await WaitForDrainAsync(edgeToHubTracker, hubToEdgeTracker, runCancellationSource.Token);

            var measurementWindow = config.Duration;
            var edgeDelta = Subtract(edgeCounters.Snapshot(1), edgeBaseline);
            var hubDelta = Subtract(hubCounters.Snapshot(1), hubBaseline);

            receiverCancellationSource.Cancel();
            await Task.WhenAll(IgnoreCancellation(gameReceiveTask), IgnoreCancellation(clientReceiveTask));

            var result = new BenchResult(
                IsSuccessful: edgeToHubTracker.Corrupted == 0 && edgeToHubTracker.Duplicated == 0 && edgeToHubTracker.Lost == 0 &&
                              hubToEdgeTracker.Corrupted == 0 && hubToEdgeTracker.Duplicated == 0 && hubToEdgeTracker.Lost == 0,
                Config: config,
                MeasurementWindow: measurementWindow,
                EdgeToHub: CreateDirectionMetrics("edge->hub", edgeToHubTracker, edgeDelta, measurementWindow),
                HubToEdge: CreateDirectionMetrics("hub->edge", hubToEdgeTracker, hubDelta, measurementWindow));

            return result;
        }
        finally
        {
            runCancellationSource.Cancel();
            await Task.WhenAll(IgnoreCancellation(hubTask), IgnoreCancellation(edgeTask));
        }
    }

    private static async Task<IPEndPoint> EstablishSessionAsync(Socket clientSocket, Socket gameSocket, int edgeGamePort, int seed, CancellationToken cancellationToken)
    {
        var payload = TrafficPayloadFactory.Create("edge->hub", sequence: -1, seed, averagePayloadBytes: 384, minPayloadBytes: 320, maxPayloadBytes: 448);
        await clientSocket.SendToAsync(payload.Bytes, SocketFlags.None, new IPEndPoint(IPAddress.Loopback, edgeGamePort), cancellationToken);
        var receiveResult = await ReceiveAsync(gameSocket, cancellationToken);
        if (!TrafficPayloadValidator.TryValidate(receiveResult.Buffer, "edge->hub", out _, out _))
        {
            throw new InvalidOperationException("Failed to establish benchmark session using seed traffic.");
        }

        return AssertEndPoint(receiveResult.RemoteEndPoint);
    }

    private static async Task SendLoopAsync(
        Socket socket,
        EndPoint destination,
        string direction,
        TrafficDirectionTracker tracker,
        BenchConfig config,
        Stopwatch stopwatch,
        TimeSpan totalDuration,
        int seed,
        CancellationToken cancellationToken)
    {
        var random = new Random(seed);
        var intervalTicks = TimeSpan.TicksPerSecond / (double)config.MessagesPerSecond;
        long sequence = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var dueAt = TimeSpan.FromTicks((long)Math.Round(sequence * intervalTicks));
            if (dueAt >= totalDuration)
            {
                break;
            }

            var delay = dueAt - stopwatch.Elapsed;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }

            var generated = TrafficPayloadFactory.Create(direction, sequence, random, config.AveragePayloadBytes, config.MinPayloadBytes, config.MaxPayloadBytes);
            var shouldMeasure = dueAt >= config.Warmup;
            if (shouldMeasure)
            {
                tracker.RecordSent(sequence, generated.Checksum);
            }

            try
            {
                await socket.SendToAsync(generated.Bytes, SocketFlags.None, destination, cancellationToken);
            }
            catch
            {
                if (shouldMeasure)
                {
                    tracker.RollbackSent(sequence);
                }

                throw;
            }

            sequence++;
        }
    }

    private static async Task ReceiveLoopAsync(Socket socket, string expectedDirection, TrafficDirectionTracker tracker, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var receiveResult = await ReceiveAsync(socket, cancellationToken);
                if (!TrafficPayloadValidator.TryValidate(receiveResult.Buffer, expectedDirection, out var payload, out var error))
                {
                    tracker.RecordCorrupted(payload, error);
                    continue;
                }

                tracker.RecordReceived(payload!.Sequence, payload.Checksum);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private static BenchDirectionMetrics CreateDirectionMetrics(string direction, TrafficDirectionTracker tracker, RuntimeStatsSnapshot delta, TimeSpan measurementWindow)
    {
        var seconds = Math.Max(measurementWindow.TotalSeconds, 0.001d);
        return new BenchDirectionMetrics(
            Direction: direction,
            MessagesSent: tracker.Sent,
            MessagesReceived: tracker.Received,
            RawBytes: delta.RawBytesIn,
            FramedBytes: delta.FramedBytesOut,
            RawFrames: delta.RawFramesOut,
            CompressedFrames: delta.CompressedFramesOut,
            RawBytesPerSecond: delta.RawBytesIn / seconds,
            FramedBytesPerSecond: delta.FramedBytesOut / seconds,
            CompressionRatio: delta.CompressionRatio,
            CompressionSavings: delta.CompressionSavings,
            Lost: tracker.Lost,
            Duplicated: tracker.Duplicated,
            Corrupted: tracker.Corrupted,
            Corruption: tracker.Corruption);
    }

    private static async Task WaitForDrainAsync(TrafficDirectionTracker edgeToHubTracker, TrafficDirectionTracker hubToEdgeTracker, CancellationToken cancellationToken)
    {
        var drainStartedAt = Stopwatch.GetTimestamp();

        while (edgeToHubTracker.HasPendingExpected || hubToEdgeTracker.HasPendingExpected)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var drainElapsed = Stopwatch.GetElapsedTime(drainStartedAt);
            if (drainElapsed >= DrainTimeout)
            {
                break;
            }

            if (edgeToHubTracker.IdleTime >= DrainIdleThreshold && hubToEdgeTracker.IdleTime >= DrainIdleThreshold)
            {
                break;
            }

            await Task.Delay(10, cancellationToken);
        }
    }

    private static RuntimeStatsSnapshot Subtract(RuntimeStatsSnapshot current, RuntimeStatsSnapshot baseline) => new(
        SessionsCreated: current.SessionsCreated - baseline.SessionsCreated,
        SessionsClosed: current.SessionsClosed - baseline.SessionsClosed,
        SessionsTimedOut: current.SessionsTimedOut - baseline.SessionsTimedOut,
        ActiveSessions: current.ActiveSessions,
        EdgePacketsIn: current.EdgePacketsIn - baseline.EdgePacketsIn,
        EdgePacketsOut: current.EdgePacketsOut - baseline.EdgePacketsOut,
        HubPacketsIn: current.HubPacketsIn - baseline.HubPacketsIn,
        HubPacketsOut: current.HubPacketsOut - baseline.HubPacketsOut,
        GamePacketsIn: current.GamePacketsIn - baseline.GamePacketsIn,
        GamePacketsOut: current.GamePacketsOut - baseline.GamePacketsOut,
        RawFramesOut: current.RawFramesOut - baseline.RawFramesOut,
        CompressedFramesOut: current.CompressedFramesOut - baseline.CompressedFramesOut,
        RawBytesIn: current.RawBytesIn - baseline.RawBytesIn,
        FramedBytesOut: current.FramedBytesOut - baseline.FramedBytesOut,
        OversizeDrop: current.OversizeDrop - baseline.OversizeDrop,
        ProtocolError: current.ProtocolError - baseline.ProtocolError,
        DecompressError: current.DecompressError - baseline.DecompressError,
        UnknownSession: current.UnknownSession - baseline.UnknownSession,
        SessionSenderMismatch: current.SessionSenderMismatch - baseline.SessionSenderMismatch,
        PortPoolExhausted: current.PortPoolExhausted - baseline.PortPoolExhausted,
        MaxSessions: current.MaxSessions);

    private static async Task IgnoreCancellation(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static Socket BindLoopbackSocket(int? port = null)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, port ?? 0));
        return socket;
    }

    private static async Task<(byte[] Buffer, EndPoint RemoteEndPoint)> ReceiveAsync(Socket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[65535];
        var result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), cancellationToken);
        return (buffer[..result.ReceivedBytes], result.RemoteEndPoint);
    }

    private static int ReserveUdpPort()
    {
        using var socket = BindLoopbackSocket();
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    private static (int startPort, int endPort) ReserveUdpPortRange(int count)
    {
        for (var startPort = 40000; startPort <= 65000 - count; startPort++)
        {
            var sockets = new List<Socket>(count);

            try
            {
                for (var offset = 0; offset < count; offset++)
                {
                    sockets.Add(BindLoopbackSocket(startPort + offset));
                }

                return (startPort, startPort + count - 1);
            }
            catch (SocketException)
            {
            }
            finally
            {
                foreach (var socket in sockets)
                {
                    socket.Dispose();
                }
            }
        }

        throw new InvalidOperationException("Failed to reserve a UDP port range.");
    }

    private static IPEndPoint AssertEndPoint(EndPoint endPoint) =>
        endPoint as IPEndPoint ?? throw new InvalidOperationException("Expected UDP receive endpoint.");

    private static async Task WaitForAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(20, cancellationToken);
        }
    }
}

internal sealed class TrafficDirectionTracker
{
    private readonly ConcurrentDictionary<long, string> _sentChecksums = new();
    private readonly ConcurrentDictionary<long, byte> _receivedSequences = new();
    private long _duplicates;
    private long _jsonInvalid;
    private long _directionMismatch;
    private long _checksumMismatch;
    private long _measurementStarted;
    private long _lastActivityTimestamp = Stopwatch.GetTimestamp();

    public long Sent => _sentChecksums.Count;

    public long Received => _receivedSequences.Count;

    public long Corrupted => Interlocked.Read(ref _jsonInvalid) + Interlocked.Read(ref _directionMismatch) + Interlocked.Read(ref _checksumMismatch);

    public long Duplicated => Interlocked.Read(ref _duplicates);

    public long Lost => Math.Max(0, Sent - Received);

    public BenchCorruptionMetrics Corruption => new(
        JsonInvalid: Interlocked.Read(ref _jsonInvalid),
        DirectionMismatch: Interlocked.Read(ref _directionMismatch),
        ChecksumMismatch: Interlocked.Read(ref _checksumMismatch));

    public bool HasPendingExpected => Sent > Received;

    public TimeSpan IdleTime => Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastActivityTimestamp));

    public void BeginMeasurement()
    {
        Interlocked.Exchange(ref _measurementStarted, 1);
        Touch();
    }

    public void RecordSent(long sequence, string checksum)
    {
        if (_sentChecksums.TryAdd(sequence, checksum))
        {
            Touch();
        }
    }

    public void RollbackSent(long sequence)
    {
        if (_sentChecksums.TryRemove(sequence, out _))
        {
            Touch();
        }
    }

    public void RecordReceived(long sequence, string checksum)
    {
        if (!_sentChecksums.TryGetValue(sequence, out var expectedChecksum))
        {
            return;
        }

        if (!string.Equals(expectedChecksum, checksum, StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _checksumMismatch);
            Touch();
            return;
        }

        if (!_receivedSequences.TryAdd(sequence, 0))
        {
            Interlocked.Increment(ref _duplicates);
        }

        Touch();
    }

    public void RecordCorrupted(TrafficPayload? payload, TrafficPayloadValidationError error)
    {
        if (payload is not null && !_sentChecksums.ContainsKey(payload.Sequence))
        {
            return;
        }

        switch (error)
        {
            case TrafficPayloadValidationError.ChecksumMismatch:
                if (payload is not null)
                {
                    Interlocked.Increment(ref _checksumMismatch);
                    Touch();
                }

                break;
            case TrafficPayloadValidationError.UnexpectedDirection:
                if (payload is not null)
                {
                    Interlocked.Increment(ref _directionMismatch);
                    Touch();
                }

                break;
            case TrafficPayloadValidationError.JsonInvalid:
            case TrafficPayloadValidationError.NullPayload:
                if (Interlocked.Read(ref _measurementStarted) == 1)
                {
                    Interlocked.Increment(ref _jsonInvalid);
                    Touch();
                }

                break;
        }
    }

    private void Touch() => Interlocked.Exchange(ref _lastActivityTimestamp, Stopwatch.GetTimestamp());
}

public static class TrafficPayloadFactory
{
    public static GeneratedTrafficPayload Create(string direction, long sequence, int seed, int averagePayloadBytes, int minPayloadBytes, int maxPayloadBytes) =>
        Create(direction, sequence, new Random(seed ^ HashCode.Combine(direction, sequence)), averagePayloadBytes, minPayloadBytes, maxPayloadBytes);

    public static GeneratedTrafficPayload Create(string direction, long sequence, Random random, int averagePayloadBytes, int minPayloadBytes, int maxPayloadBytes)
    {
        var targetBytes = SampleTargetSize(random, averagePayloadBytes, minPayloadBytes, maxPayloadBytes);
        var flags = BuildFlags(random, targetBytes);
        var tags = BuildTags(random, targetBytes);
        var traceId = $"{sequence:x8}-{random.Next(0, 65536):x4}";
        var accountId = random.Next(1, 1000);
        var roomId = random.Next(1, 128);
        var samples = BuildSamples(random, Math.Min(targetBytes, maxPayloadBytes));
        var unsigned = BuildUnsigned(direction, sequence, traceId, accountId, roomId, flags, tags, samples, paddingLength: 0);
        var signed = AddChecksum(unsigned);

        while (signed.Bytes.Length > maxPayloadBytes && (samples.Length > 1 || tags.Length > 1))
        {
            if (samples.Length > 1)
            {
                samples = samples[..^1];
            }
            else
            {
                tags = tags[..^1];
            }

            unsigned = BuildUnsigned(direction, sequence, traceId, accountId, roomId, flags, tags, samples, paddingLength: 0);
            signed = AddChecksum(unsigned);
        }

        if (signed.Bytes.Length > maxPayloadBytes)
        {
            throw new InvalidOperationException($"Failed to generate benchmark payload within {minPayloadBytes}-{maxPayloadBytes} bytes.");
        }

        var minPadding = Math.Max(0, minPayloadBytes - signed.Bytes.Length);
        var maxPadding = Math.Max(0, maxPayloadBytes - signed.Bytes.Length);
        var targetPadding = Math.Clamp(targetBytes - signed.Bytes.Length, minPadding, maxPadding);

        unsigned = BuildUnsigned(direction, sequence, traceId, accountId, roomId, flags, tags, samples, targetPadding);
        signed = AddChecksum(unsigned);

        if (signed.Bytes.Length < minPayloadBytes || signed.Bytes.Length > maxPayloadBytes)
        {
            throw new InvalidOperationException($"Generated benchmark payload length {signed.Bytes.Length} outside {minPayloadBytes}-{maxPayloadBytes} bytes.");
        }

        return signed;
    }

    private static int[] BuildFlags(Random random, int targetBytes)
    {
        var flagCount = Math.Clamp(targetBytes / 240, 0, 3);
        var flags = new int[flagCount];
        for (var index = 0; index < flagCount; index++)
        {
            flags[index] = random.Next(0, 32);
        }

        return flags;
    }

    private static int[] BuildSamples(Random random, int targetBytes)
    {
        var sampleCount = Math.Clamp(targetBytes / 96, 0, 80);
        var samples = new int[sampleCount];
        for (var index = 0; index < sampleCount; index++)
        {
            samples[index] = random.Next(0, 10000);
        }

        return samples;
    }

    private static string[] BuildTags(Random random, int targetBytes)
    {
        var tagCount = Math.Clamp(targetBytes / 220, 0, 4);
        var tags = new string[tagCount];
        for (var index = 0; index < tagCount; index++)
        {
            tags[index] = $"tag-{index}-{random.Next(10, 99)}";
        }

        return tags;
    }

    private static TrafficPayloadUnsigned BuildUnsigned(string direction, long sequence, string traceId, int accountId, int roomId, int[] flags, string[] tags, int[] samples, int paddingLength)
    {
        return new TrafficPayloadUnsigned(
            SchemaVersion: 1,
            Direction: direction,
            Sequence: sequence,
            SentAtUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            TraceId: traceId,
            AccountId: accountId,
            RoomId: roomId,
            Flags: flags,
            Samples: samples,
            Tags: tags,
            Padding: new string('x', Math.Max(0, paddingLength)));
    }

    private static GeneratedTrafficPayload AddChecksum(TrafficPayloadUnsigned unsigned)
    {
        var checksum = ComputeChecksum(unsigned);
        var payload = new TrafficPayload(
            unsigned.SchemaVersion,
            unsigned.Direction,
            unsigned.Sequence,
            unsigned.SentAtUnixMs,
            unsigned.TraceId,
            unsigned.AccountId,
            unsigned.RoomId,
            unsigned.Flags,
            unsigned.Samples,
            unsigned.Tags,
            unsigned.Padding,
            checksum);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, TrafficPayloadJsonContext.Default.TrafficPayload);
        return new GeneratedTrafficPayload(payload, checksum, bytes);
    }

    internal static string ComputeChecksum(TrafficPayloadUnsigned payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, TrafficPayloadJsonContext.Default.TrafficPayloadUnsigned);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private static int SampleTargetSize(Random random, int averagePayloadBytes, int minPayloadBytes, int maxPayloadBytes)
    {
        if (minPayloadBytes == maxPayloadBytes)
        {
            return minPayloadBytes;
        }

        var left = random.NextDouble();
        var right = random.NextDouble();
        var mode = (averagePayloadBytes - minPayloadBytes) / (double)(maxPayloadBytes - minPayloadBytes);
        var sample = left < mode
            ? minPayloadBytes + Math.Sqrt(left * right) * (averagePayloadBytes - minPayloadBytes)
            : maxPayloadBytes - Math.Sqrt((1d - left) * (1d - right)) * (maxPayloadBytes - averagePayloadBytes);
        return Math.Clamp((int)Math.Round(sample), minPayloadBytes, maxPayloadBytes);
    }
}

public static class TrafficPayloadValidator
{
    public static bool TryValidate(byte[] buffer, string expectedDirection, out TrafficPayload? payload, out TrafficPayloadValidationError error)
    {
        payload = null;
        error = TrafficPayloadValidationError.None;

        try
        {
            payload = JsonSerializer.Deserialize(buffer, TrafficPayloadJsonContext.Default.TrafficPayload);
            if (payload is null)
            {
                error = TrafficPayloadValidationError.NullPayload;
                return false;
            }

            if (!string.Equals(payload.Direction, expectedDirection, StringComparison.Ordinal))
            {
                error = TrafficPayloadValidationError.UnexpectedDirection;
                return false;
            }

            var checksum = TrafficPayloadFactory.ComputeChecksum(new TrafficPayloadUnsigned(
                payload.SchemaVersion,
                payload.Direction,
                payload.Sequence,
                payload.SentAtUnixMs,
                payload.TraceId,
                payload.AccountId,
                payload.RoomId,
                payload.Flags,
                payload.Samples,
                payload.Tags,
                payload.Padding));
            if (!string.Equals(payload.Checksum, checksum, StringComparison.Ordinal))
            {
                error = TrafficPayloadValidationError.ChecksumMismatch;
                return false;
            }

            return true;
        }
        catch (JsonException)
        {
            error = TrafficPayloadValidationError.JsonInvalid;
            return false;
        }
    }
}

public enum TrafficPayloadValidationError
{
    None = 0,
    JsonInvalid = 1,
    NullPayload = 2,
    UnexpectedDirection = 3,
    ChecksumMismatch = 4,
}

public sealed record GeneratedTrafficPayload(TrafficPayload Payload, string Checksum, byte[] Bytes);

public sealed record TrafficPayload(
    int SchemaVersion,
    string Direction,
    long Sequence,
    long SentAtUnixMs,
    string TraceId,
    int AccountId,
    int RoomId,
    int[] Flags,
    int[] Samples,
    string[] Tags,
    string Padding,
    string Checksum);

public sealed record TrafficPayloadUnsigned(
    int SchemaVersion,
    string Direction,
    long Sequence,
    long SentAtUnixMs,
    string TraceId,
    int AccountId,
    int RoomId,
    int[] Flags,
    int[] Samples,
    string[] Tags,
    string Padding);

[JsonSerializable(typeof(BenchCorruptionMetrics))]
[JsonSerializable(typeof(BenchConfig))]
[JsonSerializable(typeof(BenchDirectionMetrics))]
[JsonSerializable(typeof(BenchResult))]
[JsonSerializable(typeof(TrafficPayload))]
[JsonSerializable(typeof(TrafficPayloadUnsigned))]
internal sealed partial class TrafficPayloadJsonContext : JsonSerializerContext
{
}

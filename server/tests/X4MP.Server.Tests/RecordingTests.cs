using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Net;
using X4MP.Core.Net;
using X4MP.Proto;
using X4MP.Protocol;
using X4MP.Transport;
using X4MP.Transport.Recording;
using X4MP.Server.Hosting;

namespace X4MP.Server.Tests;

/// <summary>
/// M4-26/27/28 (docs/m4-plan.md W6): the <c>.x4rec</c> format, the recorder on the outbound choke point, replay
/// into a sink, and the divergence count a mirror run reports.
/// </summary>
public sealed class RecordingTests : IDisposable
{
    private readonly string _dir;

    public RecordingTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "x4mp-rec-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not a test failure.
        }
    }

    /// <summary>A duplex pipe whose two directions are separate <see cref="Pipe"/> instances.</summary>
    private sealed class DuplexPipe(Pipe inbound, Pipe outbound) : IDuplexPipe
    {
        public PipeReader Input => inbound.Reader;

        public PipeWriter Output => outbound.Writer;
    }

    /// <summary>A payload whose first four bytes carry <paramref name="seed"/>, so order survives a round trip.</summary>
    private static byte[] Payload(int seed, int length)
    {
        var payload = new byte[length];
        BinaryPrimitives.WriteInt32LittleEndian(payload, seed);
        for (int i = 4; i < length; i++)
        {
            payload[i] = (byte)(seed + i);
        }

        return payload;
    }

    private MessageRecorderOptions Options(long maxPartBytes = RecordingFormat.DefaultMaxPartBytes) =>
        new() { Directory = _dir, MaxPartBytes = maxPartBytes };

    [Fact]
    public void RecorderWritesAPartAReaderCanParse()
    {
        var expected = new List<(MsgType Type, Lane Lane, uint Stream, byte[] Payload)>();
        var recorder = new MessageRecorder(Options());
        try
        {
            for (int i = 0; i < 5; i++)
            {
                var type = i % 2 == 0 ? MsgType.WorldUpdate : MsgType.Ping;
                var lane = i % 2 == 0 ? Lane.Realtime : Lane.Control;
                var payload = Payload(i, 16 + i);
                recorder.Record(type, lane, RecordingMark.None, (uint)(i + 1), payload);
                expected.Add((type, lane, (uint)(i + 1), payload));
            }

            Assert.Equal(5, recorder.Records);
            Assert.Equal(1, recorder.Parts);
            recorder.Flush();
            Assert.NotNull(recorder.CurrentPath);
        }
        finally
        {
            recorder.Dispose();
        }

        var parts = RecordingReader.EnumerateSeries(_dir);
        Assert.Single(parts);
        Assert.Equal(Path.Combine(_dir, "x4mp-rec-00001.x4rec"), parts[0]);

        using var reader = RecordingReader.Open(parts[0]);
        Assert.Equal(RecordingFormat.Version, reader.Header.Version);
        Assert.Equal(RecordingFormat.ProtocolStamp, reader.Header.ProtocolStamp);

        var actual = new List<RecordingRecord>();
        while (reader.TryRead(out var record))
        {
            actual.Add(record);
        }

        Assert.Equal(expected.Count, actual.Count);
        Assert.Equal(5, reader.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].Type, actual[i].Type);
            Assert.Equal(expected[i].Lane, actual[i].Lane);
            Assert.Equal(expected[i].Stream, actual[i].Stream);
            Assert.Equal(expected[i].Payload, actual[i].Payload);
        }

        // Timestamps are monotonic and the last one is what the reader reports.
        for (int i = 1; i < actual.Count; i++)
        {
            Assert.True(actual[i].TimeUs >= actual[i - 1].TimeUs);
        }

        Assert.Equal(actual[^1].TimeUs, reader.LastTimeUs);
    }

    [Fact]
    public void PartsRotateBySizeAndReadBackInRotationOrder()
    {
        int parts;
        using (var recorder = new MessageRecorder(Options(maxPartBytes: 2048)))
        {
            for (int i = 0; i < 40; i++)
            {
                recorder.Record(MsgType.WorldUpdate, Lane.Realtime, RecordingMark.Datagram, 7, Payload(i, 200));
            }

            parts = recorder.Parts;
            Assert.True(parts > 1, $"expected more than one part, got {parts}");
        }

        var files = RecordingReader.EnumerateSeries(_dir);
        Assert.Equal(parts, files.Count);

        // Every part starts with its own file header, and the payloads keep the order they were written in.
        var all = RecordingReader.ReadSeries(_dir);
        Assert.Equal(40, all.Count);
        for (int i = 0; i < all.Count; i++)
        {
            Assert.Equal(i, BinaryPrimitives.ReadInt32LittleEndian(all[i].Payload));
            Assert.Equal(RecordingMark.Datagram, all[i].Flags);
            Assert.Equal(7u, all[i].Stream);
        }

        // A restart continues the numbering instead of overwriting the previous recording.
        Assert.Equal(parts + 1, MessageRecorder.NextFreePartNumber(_dir, RecordingFormat.DefaultPrefix));
        using (var second = new MessageRecorder(Options(maxPartBytes: 2048)))
        {
            second.Record(MsgType.Ping, Lane.Control, RecordingMark.None, 1, Payload(100, 32));
            Assert.Equal(parts + 1, second.Parts);
        }

        Assert.Equal(parts + 1, RecordingReader.EnumerateSeries(_dir).Count);
    }

    [Fact]
    public void TrySendFeedsTheRecorder()
    {
        var payload = Payload(7, 32);
        using var recorder = new MessageRecorder(Options());
        using var connection = new PipeNodeConnection(
            new DuplexPipe(new Pipe(), new Pipe()),
            new IPEndPoint(IPAddress.Loopback, 40000),
            new NetOptions());
        connection.SendObserver = recorder.Record;

        var result = connection.TrySend(OutboundFrame.Create(MsgType.WorldUpdate, payload));
        Assert.Equal(SendResult.Queued, result);
        Assert.Equal(1, recorder.Records);

        recorder.Flush();
        var record = RecordingReader.ReadAll(RecordingReader.EnumerateSeries(_dir).Single()).Single();
        Assert.Equal(MsgType.WorldUpdate, record.Type);
        Assert.Equal(Lane.Realtime, record.Lane);
        Assert.Equal(RecordingMark.None, record.Flags);
        Assert.Equal(payload, record.Payload);
    }

    [Fact]
    public void RecordedPayloadReEncodesToTheOriginalFrame()
    {
        var payload = Payload(3, 40);
        byte[] original = FrameCodec.Encode(MsgType.WorldUpdate, payload);
        using (var recorder = new MessageRecorder(Options()))
        {
            recorder.Record(MsgType.WorldUpdate, Lane.Realtime, RecordingMark.None, 2, payload);
            recorder.Flush();
        }

        var record = RecordingReader.ReadSeries(_dir).Single();
        Assert.Equal(original, record.EncodeFrame());
    }

    [Fact]
    public async Task ReplayReproducesTheRecordedCounts()
    {
        using (var recorder = new MessageRecorder(Options()))
        {
            for (int i = 0; i < 6; i++)
            {
                recorder.Record(MsgType.SectorComplete, Lane.Control, RecordingMark.None, 1, Payload(i, 24));
            }

            for (int i = 0; i < 4; i++)
            {
                recorder.Record(MsgType.WorldUpdate, Lane.Realtime, RecordingMark.Datagram, 2, Payload(10 + i, 48));
            }

            recorder.Flush();
        }

        var recorded = RecordingReader.ReadSeries(_dir);
        var replayer = new RecordingReplayer(recorded) { Mode = ReplayMode.AsFastAsPossible };
        var delivered = new List<RecordingRecord>();
        await replayer.RunAsync(record => delivered.Add(record));

        Assert.Equal(recorded.Count, delivered.Count);
        Assert.Equal(6, replayer.Stats.CountOf(MsgType.SectorComplete));
        Assert.Equal(4, replayer.Stats.CountOf(MsgType.WorldUpdate));
        Assert.Equal(2, replayer.Stats.Streams);
        Assert.Equal(ReplayStats.Of(recorded).Describe(), replayer.Stats.Describe());
        Assert.True(RecordingDivergence.Compare(recorded, delivered).Identical);
    }

    [Fact]
    public async Task MirrorReplayReproducesTheAuthorityStream()
    {
        string authorityDir = Path.Combine(_dir, "authority");
        string mirrorDir = Path.Combine(_dir, "mirror");

        // The authority records its session (M4-26).
        using (var recorder = new MessageRecorder(new MessageRecorderOptions { Directory = authorityDir }))
        {
            for (int i = 0; i < 8; i++)
            {
                recorder.Record(MsgType.SectorComplete, Lane.Control, RecordingMark.None, 1, Payload(i, 32));
            }

            for (int i = 0; i < 12; i++)
            {
                recorder.Record(MsgType.WorldUpdate, Lane.Realtime, RecordingMark.Datagram, 2, Payload(20 + i, 64));
            }

            recorder.Flush();
        }

        var authority = RecordingReader.ReadSeries(authorityDir);
        Assert.Equal(20, authority.Count);

        // The mirror replays that stream into a node connection and records what it actually sent (M4-27 + M4-28).
        using var mirrorRecorder = new MessageRecorder(new MessageRecorderOptions { Directory = mirrorDir });
        using (var connection = new PipeNodeConnection(
            new DuplexPipe(new Pipe(), new Pipe()),
            new IPEndPoint(IPAddress.Loopback, 40001),
            new NetOptions()))
        {
            connection.SendObserver = mirrorRecorder.Record;
            var replayer = new RecordingReplayer(authority);
            await replayer.RunIntoAsync(connection);
            Assert.Equal(authority.Count, replayer.Stats.Total);
        }

        mirrorRecorder.Flush();
        mirrorRecorder.Dispose();

        var mirror = RecordingReader.ReadSeries(mirrorDir);
        var report = RecordingDivergence.Compare(authority, mirror, compareStreams: false);
        Assert.True(report.Identical, report.Describe());
        Assert.Equal(0, report.Divergence);
        Assert.Equal(8, ReplayStats.Of(mirror).CountOf(MsgType.SectorComplete));
        Assert.Equal(12, ReplayStats.Of(mirror).CountOf(MsgType.WorldUpdate));
    }

    [Fact]
    public void ATenMinuteSessionRecordsAndReadsBackWithoutADecodeError()
    {
        // The M4-26 AC at the size a real session reaches: 20 Hz realtime for 4 nodes plus a control message every
        // 100 ms, over 10 minutes - about 50k messages and several rotated parts.
        const int RealtimeHz = 20;
        const int Nodes = 4;
        const int Seconds = 600;
        long expected = 0;
        using (var recorder = new MessageRecorder(Options(maxPartBytes: 1 << 20)))
        {
            for (int tick = 0; tick < Seconds * RealtimeHz; tick++)
            {
                long timeUs = tick * 50_000L;
                for (uint node = 1; node <= Nodes; node++)
                {
                    recorder.Record(timeUs, MsgType.WorldUpdate, Lane.Realtime, RecordingMark.Datagram, node, Payload(tick, 64));
                    expected++;
                }

                if (tick % 2 == 0)
                {
                    recorder.Record(timeUs, MsgType.SectorComplete, Lane.Control, RecordingMark.None, 1, Payload(tick, 96));
                    expected++;
                }
            }

            Assert.Equal(expected, recorder.Records);
            Assert.True(recorder.Parts > 1, $"a 10-minute session must rotate, got {recorder.Parts} part(s)");
            recorder.Flush();
        }

        var parts = RecordingReader.EnumerateSeries(_dir);
        var records = RecordingReader.ReadSeries(_dir);
        Assert.Equal(expected, records.Count);
        Assert.True(parts.Count > 1);

        // The timestamps survive in order, so a real-time replay reproduces the original pacing.
        long first = records[0].TimeUs;
        long last = records[^1].TimeUs;
        Assert.Equal(0, first);
        Assert.Equal((Seconds * RealtimeHz - 1) * 50_000L, last);
        Assert.Equal(TimeSpan.FromMicroseconds((Seconds * RealtimeHz - 1) * 50_000L), TimeSpan.FromMicroseconds(last - first));

        // And the stream replays with the same counts.
        var stats = ReplayStats.Of(records);
        Assert.Equal(Seconds * RealtimeHz * Nodes, stats.CountOf(MsgType.WorldUpdate));
        Assert.Equal(Seconds * RealtimeHz / 2, stats.CountOf(MsgType.SectorComplete));
        Assert.Equal(Nodes, stats.Streams);
    }

    [Fact]
    public void DivergenceCountsEveryKindOfDrift()
    {
        var authority = new List<RecordingRecord>
        {
            new(0, MsgType.SectorComplete, Lane.Control, RecordingMark.None, 1, Payload(0, 20)),
            new(1_000, MsgType.WorldUpdate, Lane.Realtime, RecordingMark.Datagram, 2, Payload(1, 20)),
            new(2_000, MsgType.WorldUpdate, Lane.Realtime, RecordingMark.Datagram, 2, Payload(2, 20)),
        };

        Assert.True(RecordingDivergence.Compare(authority, authority).Identical);

        // One payload changed in place.
        var corrupted = new List<RecordingRecord>(authority)
        {
            [1] = authority[1] with { Payload = Payload(99, 20) },
        };
        var payloadReport = RecordingDivergence.Compare(authority, corrupted);
        Assert.Equal(1, payloadReport.PayloadMismatches);
        Assert.Equal(1, payloadReport.FirstDivergenceIndex);
        Assert.Equal(1, payloadReport.Divergence);

        // One message lost.
        var missingReport = RecordingDivergence.Compare(authority, authority.Take(2).ToList());
        Assert.Equal(1, missingReport.Missing);
        Assert.Equal(2, missingReport.FirstDivergenceIndex);

        // One message the authority never sent.
        var extraReport = RecordingDivergence.Compare(authority, [.. authority, authority[0]]);
        Assert.Equal(1, extraReport.Extra);

        // Same payload, different destination.
        var fieldReport = RecordingDivergence.Compare(authority, [authority[0], authority[1] with { Stream = 9 }, authority[2]]);
        Assert.Equal(1, fieldReport.FieldMismatches);
        Assert.Equal(0, fieldReport.PayloadMismatches);

        // Per-type count deltas name the type that drifted.
        Assert.Empty(payloadReport.TypeCountDeltas);
        Assert.Equal(1, missingReport.TypeCountDeltas[MsgType.WorldUpdate]);
    }

    [Fact]
    public void ReaderRejectsAForeignFileAndATruncatedRecord()
    {
        string foreign = Path.Combine(_dir, "foreign.x4rec");
        File.WriteAllBytes(foreign, new byte[RecordingFormat.FileHeaderSize + 32]);
        Assert.Throws<InvalidDataException>(() => RecordingReader.Open(foreign));

        using (var recorder = new MessageRecorder(Options()))
        {
            recorder.Record(MsgType.Ping, Lane.Control, RecordingMark.None, 1, Payload(5, 40));
            recorder.Flush();
        }

        string part = RecordingReader.EnumerateSeries(_dir).Single();
        byte[] bytes = File.ReadAllBytes(part);
        Assert.Equal(RecordingFormat.FileHeaderSize + RecordingFormat.RecordSize(40), bytes.Length);

        string truncated = Path.Combine(_dir, "x4mp-rec-00099.x4rec");
        File.WriteAllBytes(truncated, bytes[..(RecordingFormat.FileHeaderSize + RecordingFormat.RecordHeaderSize + 5)]);
        using var reader = RecordingReader.Open(truncated);
        Assert.Throws<InvalidDataException>(() => reader.TryRead(out _));
    }

    [Fact]
    public void RecordInfoReportsThePartsAndMessageCounts()
    {
        RecordSampleStream();

        var (code, output) = RunRecord("info", _dir);
        Assert.Equal(0, code);
        Assert.Contains("parts=1", output, StringComparison.Ordinal);
        Assert.Contains("WorldUpdate=2", output, StringComparison.Ordinal);
        Assert.Contains("SectorComplete=1", output, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordVerifyReEncodesEveryMessage()
    {
        RecordSampleStream();

        var (code, output) = RunRecord("verify", _dir);
        Assert.Equal(0, code);
        Assert.Contains("ok messages=3", output, StringComparison.Ordinal);
        Assert.Contains("reencoded=3", output, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordCompareReportsIdenticalStreamsAndDivergence()
    {
        RecordSampleStream();
        string mirrorDir = Path.Combine(_dir, "mirror");
        using (var recorder = new MessageRecorder(new MessageRecorderOptions { Directory = mirrorDir }))
        {
            recorder.Record(0, MsgType.WorldUpdate, Lane.Realtime, RecordingMark.None, 1, Payload(1, 64));
            recorder.Record(50_000, MsgType.WorldUpdate, Lane.Realtime, RecordingMark.None, 1, Payload(2, 64));
            recorder.Record(100_000, MsgType.SectorComplete, Lane.Control, RecordingMark.None, 1, Payload(3, 96));
            recorder.Flush();
        }

        var (identicalCode, identicalOutput) = RunRecord("compare", _dir, mirrorDir);
        Assert.Equal(0, identicalCode);
        Assert.Contains("divergence=0", identicalOutput, StringComparison.Ordinal);

        // A mirror that lost its last message is a divergence, and the command says so with a non-zero code.
        string shortDir = Path.Combine(_dir, "short");
        using (var recorder = new MessageRecorder(new MessageRecorderOptions { Directory = shortDir }))
        {
            recorder.Record(0, MsgType.WorldUpdate, Lane.Realtime, RecordingMark.None, 1, Payload(1, 64));
            recorder.Flush();
        }

        var (divergedCode, divergedOutput) = RunRecord("compare", _dir, shortDir);
        Assert.Equal(1, divergedCode);
        Assert.Contains("divergence=2", divergedOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordUsageNeedsAKnownSubcommand()
    {
        Assert.Equal(2, RunRecord("nonsense", _dir).Code);
        Assert.Equal(2, RunRecord("info").Code);
        Assert.Equal(2, RunRecord("compare", _dir).Code);
    }

    private void RecordSampleStream()
    {
        using var recorder = new MessageRecorder(Options());
        recorder.Record(0, MsgType.WorldUpdate, Lane.Realtime, RecordingMark.Datagram, 1, Payload(1, 64));
        recorder.Record(50_000, MsgType.WorldUpdate, Lane.Realtime, RecordingMark.Datagram, 1, Payload(2, 64));
        recorder.Record(100_000, MsgType.SectorComplete, Lane.Control, RecordingMark.None, 1, Payload(3, 96));
        recorder.Flush();
    }

    private static (int Code, string Output) RunRecord(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        int code = RecordCommands.Run(args, output, error);
        return (code, output.ToString() + error.ToString());
    }
}

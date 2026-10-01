using System.Text.Json;
using X4MP.Proto;

namespace X4MP.Protocol.Tests;

public class GoldenVectorTests
{
    private const string UpdateVariable = "X4MP_UPDATE_GOLDEN";

    [Fact]
    public void CommittedVectorsMatchRegeneration()
    {
        var committed = GoldenVectors.TestDataDirectory;
        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            GoldenVectors.WriteTo(committed);
            return;
        }

        var tmp = Path.Combine(Path.GetTempPath(), "x4mp-golden-" + Guid.NewGuid().ToString("N"));
        try
        {
            GoldenVectors.WriteTo(tmp);
            var expected = Directory.EnumerateFiles(tmp).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
            var actual = Directory.Exists(committed)
                ? Directory.EnumerateFiles(committed).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()
                : [];

            var problems = new List<string>();
            problems.AddRange(expected.Except(actual).Select(f => $"missing from protocol/testdata: {f}"));
            problems.AddRange(actual.Except(expected).Select(f => $"stale file in protocol/testdata: {f}"));
            foreach (var name in expected.Intersect(actual))
            {
                var a = File.ReadAllBytes(Path.Combine(tmp, name!));
                var b = File.ReadAllBytes(Path.Combine(committed, name!));
                if (name!.EndsWith(".json", StringComparison.Ordinal))
                    b = NormaliseNewlines(b);
                if (!a.AsSpan().SequenceEqual(b))
                    problems.Add($"differs: {name}");
            }

            Assert.True(problems.Count == 0,
                $"Golden vectors are out of date ({problems.Count} problem(s)). Regenerate with: {UpdateVariable}=1 dotnet test server/tests/X4MP.Protocol.Tests --filter GoldenVectorTests\n"
                + string.Join("\n", problems.Take(20)));
        }
        finally
        {
            if (Directory.Exists(tmp))
                Directory.Delete(tmp, recursive: true);
        }
    }

    private static byte[] NormaliseNewlines(byte[] data) =>
        System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(data).Replace("\r\n", "\n"));

    [Fact]
    public void RegenerationIsDeterministic()
    {
        var a = GoldenVectors.GenerateFiles();
        var b = GoldenVectors.GenerateFiles();
        Assert.Equal(a.Keys, b.Keys);
        foreach (var k in a.Keys)
            Assert.Equal(a[k], b[k]);
    }

    [Fact]
    public void EveryMsgTypeHasAtLeastOneFrameVector()
    {
        var covered = GoldenVectors.Build().Where(v => v.Kind == "frame").Select(v => v.File[..6]).ToHashSet();
        foreach (var d in MessageRegistry.Default.Descriptors)
            Assert.Contains($"0x{(ushort)d.Type:X4}", covered);
    }

    [Fact]
    public void IndexJsonIsValidAndListsEveryFile()
    {
        var files = GoldenVectors.GenerateFiles();
        using var doc = JsonDocument.Parse(files["index.json"]);
        var listed = doc.RootElement.GetProperty("vectors").EnumerateArray().Select(e => e.GetProperty("file").GetString()!).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(files.Keys.Where(k => k != "index.json").ToArray(), listed);
        Assert.Equal(0, doc.RootElement.GetProperty("protocol").GetProperty("major").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("protocol").GetProperty("minor").GetInt32());
        Assert.True(doc.RootElement.GetProperty("quantize").GetProperty("cases").GetArrayLength() > 50);
    }

    [Fact]
    public void GoodFrameVectorsDecodeAndMatchTheirSamples()
    {
        foreach (var s in SampleMessages.All)
        {
            var bytes = GoldenVectors.GenerateFiles()[$"{s.Stem}.bin"];
            Assert.True(FrameCodec.TryDecode(bytes, out var frame, out int consumed));
            Assert.Equal(bytes.Length, consumed);
            Assert.Equal(s.SourceJson, SampleMessage.ToJson(MessageRegistry.Default.Decode(frame)));
        }
    }

    [Fact]
    public void RejectVectorsAreRejectedWithTheDeclaredCode()
    {
        var files = GoldenVectors.GenerateFiles();
        using var doc = JsonDocument.Parse(files["index.json"]);
        int checkedCount = 0;
        foreach (var v in doc.RootElement.GetProperty("vectors").EnumerateArray())
        {
            var kind = v.GetProperty("kind").GetString();
            if (kind is not ("reject-frame" or "reject-replication-entries" or "reject-datagram"))
                continue;

            var bytes = files[v.GetProperty("file").GetString()!];
            var expected = Enum.Parse<ViolationCode>(v.GetProperty("expect").GetString()!);
            var ex = Assert.Throws<ProtocolViolation>(() =>
            {
                switch (kind)
                {
                    case "reject-frame":
                        if (!FrameCodec.TryDecode(bytes, out var frame, out _))
                            throw new ProtocolViolation(ViolationCode.TruncatedFrame, "end of stream mid-frame");
                        MessageRegistry.Default.Decode(frame);
                        break;
                    case "reject-replication-entries":
                        ReplicationCodec.Decode(bytes, v.GetProperty("entryCount").GetInt32());
                        break;
                    default:
                        DatagramCodec.ReadSubMessages(bytes);
                        DatagramCodec.ReadHeader(bytes);
                        break;
                }
            });
            Assert.Equal(expected, ex.Code);
            checkedCount++;
        }
        Assert.True(checkedCount >= 18, $"only {checkedCount} reject vectors");
    }

    [Fact]
    public void ReplicationVectorsDecodeAndReEncodeIdentically()
    {
        var files = GoldenVectors.GenerateFiles();
        using var doc = JsonDocument.Parse(files["index.json"]);
        int n = 0;
        foreach (var v in doc.RootElement.GetProperty("vectors").EnumerateArray().Where(e => e.GetProperty("kind").GetString() == "replication-entries"))
        {
            var bytes = files[v.GetProperty("file").GetString()!];
            var entries = ReplicationCodec.Decode(bytes, v.GetProperty("entryCount").GetInt32());
            Assert.Equal(bytes, ReplicationCodec.Encode([.. entries]));
            n++;
        }
        Assert.Equal(8, n);
    }

    [Fact]
    public void AllMasksVectorCoversEveryMaskOnce()
    {
        var bytes = GoldenVectors.GenerateFiles()["repl_all_masks.bin"];
        var entries = ReplicationCodec.Decode(bytes, 256);
        Assert.Equal(Enumerable.Range(0, 256).Select(m => (byte)m), entries.Select(e => (byte)e.Mask));
    }

    [Fact]
    public void DatagramVectorsDecode()
    {
        var files = GoldenVectors.GenerateFiles();
        var mixed = files["dgram_mixed.bin"];
        Assert.Equal(7u, DatagramCodec.ReadHeader(mixed).Seq);
        var subs = DatagramCodec.ReadSubMessages(mixed);
        Assert.Equal([MsgType.Ping, MsgType.PlayerState, MsgType.Replication, MsgType.WorldUpdate], subs.Select(s => s.Type).ToArray());
        foreach (var s in subs)
            MessageRegistry.Default.Decode(s.Type, mixed.AsSpan(s.Offset, s.Length).ToArray());
        Assert.Equal(24, files["dgram_ack_only.bin"].Length);
    }

    [Fact]
    public void ReplicationMessageSampleCarriesValidCodecEntries()
    {
        var sample = SampleMessages.All.Single(s => s.Type == MsgType.Replication);
        var rep = (Replication)MessageRegistry.Default.Decode(MsgType.Replication, sample.Encode());
        var entries = ReplicationCodec.Decode(rep.GetEntriesArray(), rep.EntryCount);
        Assert.Equal([1001u, 1002u], entries.Select(e => e.NetId).ToArray());
    }
}

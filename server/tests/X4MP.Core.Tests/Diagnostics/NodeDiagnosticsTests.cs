using X4MP.Core.Diagnostics;
using X4MP.Proto;

namespace X4MP.Core.Tests.Diagnostics;

public sealed class SelfTestParserTests
{
    [Theory]
    [InlineData("SELFTEST PASS  x4native.api     game 9.00 exports 15/15", "PASS", "x4native.api", "game 9.00 exports 15/15")]
    [InlineData("SELFTEST FAIL  game.adapter     probe failed: no menu", "FAIL", "game.adapter", "probe failed: no menu")]
    [InlineData("SELFTEST WARN  saves.block      ", "WARN", "saves.block", "")]
    [InlineData("SELFTEST SKIP main_thread", "SKIP", "main_thread", "")]
    [InlineData("SELFTEST PASS a  has   PASS and FAIL inside", "PASS", "a", "has   PASS and FAIL inside")]
    public void ParsesRows(string text, string result, string name, string detail)
    {
        Assert.Equal(SelfTestLineKind.Row, SelfTestParser.Parse(text, out var row));
        Assert.Equal(new SelfTestRow(result, name, detail), row);
    }

    [Theory]
    [InlineData("SELFTEST begin (trigger=config, x4mp 0.1.0)", SelfTestLineKind.Begin)]
    [InlineData("SELFTEST summary: 6 PASS, 1 FAIL, 0 WARN, 0 SKIP", SelfTestLineKind.End)]
    [InlineData("SELFTEST END", SelfTestLineKind.End)]
    [InlineData("SELFTEST forwarded 8 lines to the server", SelfTestLineKind.Other)]
    [InlineData("SELFTEST ", SelfTestLineKind.Other)]
    [InlineData("SELFTEST MAYBE x.y detail", SelfTestLineKind.Other)]
    [InlineData("SELFTEST pass x.y detail", SelfTestLineKind.Other)]
    [InlineData("SELFTEST PASS", SelfTestLineKind.Other)]
    [InlineData("hello world", SelfTestLineKind.None)]
    [InlineData("SELFTESTING PASS a b", SelfTestLineKind.None)]
    [InlineData(" SELFTEST PASS a b", SelfTestLineKind.None)]
    public void ClassifiesOtherLines(string text, SelfTestLineKind kind)
    {
        Assert.Equal(kind, SelfTestParser.Parse(text, out var row));
        Assert.Null(row);
    }

    [Fact]
    public void LongNamesAndDetailsAreHandled()
    {
        Assert.Equal(SelfTestLineKind.Other, SelfTestParser.Parse($"SELFTEST PASS {new string('n', 65)} d", out _));
        Assert.Equal(SelfTestLineKind.Row, SelfTestParser.Parse($"SELFTEST PASS {new string('n', 64)} {new string('d', 1000)}", out var row));
        Assert.Equal(SelfTestParser.MaxDetailLength, row!.Detail.Length);
    }

    [Fact]
    public void FormatMatchesTheModsPaddingAndRoundTrips()
    {
        var row = new SelfTestRow("FAIL", "team.faction", "not found");
        Assert.Equal("SELFTEST FAIL  team.faction     not found", SelfTestParser.Format(row));
        Assert.Equal(SelfTestLineKind.Row, SelfTestParser.Parse(SelfTestParser.Format(row), out var parsed));
        Assert.Equal(row, parsed);
    }
}

public sealed class NodeDiagnosticsStoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    private static List<(LogLevel Level, string Text)> Lines(params string[] texts) => [.. texts.Select(t => (LogLevel.Info, t))];

    private static List<(LogLevel Level, string Text)> Many(int count, string prefix = "line") => [.. Enumerable.Range(0, count).Select(i => (LogLevel.Info, $"{prefix} {i}"))];

    [Fact]
    public void ABurstAboveTheBucketIsCutAndCounted()
    {
        var store = new NodeDiagnosticsStore(new NodeDiagnosticsOptions { LinesPerSecond = 50, LineBurst = 100 });

        var result = store.Ingest(1, "A", Many(250), T0);

        Assert.Equal(100, result.Accepted.Count);
        Assert.Equal(150, result.Dropped);
        var s = store.Get(1)!;
        Assert.Equal((250, 150), (s.LinesReceived, s.LinesDropped));
        Assert.Equal("line 0", s.Lines[0].Text); // the first lines win, the rest of the burst is dropped
    }

    [Fact]
    public void TheBucketRefillsAtFiftyLinesPerSecond()
    {
        var store = new NodeDiagnosticsStore();
        store.Ingest(1, "A", Many(100), T0); // empties the bucket

        Assert.Empty(store.Ingest(1, "A", Many(1), T0).Accepted);
        var half = store.Ingest(1, "A", Many(100), T0.AddSeconds(1)); // 50 tokens came back
        Assert.Equal(50, half.Accepted.Count);
        Assert.Equal(50, half.Dropped);
        Assert.Equal(100, store.Ingest(1, "A", Many(100), T0.AddSeconds(10)).Accepted.Count); // capped at the burst
    }

    [Fact]
    public void EachNodeHasItsOwnBucket()
    {
        var store = new NodeDiagnosticsStore();
        store.Ingest(1, "A", Many(100), T0);

        Assert.Equal(100, store.Ingest(2, "B", Many(100), T0).Accepted.Count);
        Assert.Empty(store.Ingest(1, "A", Many(5), T0).Accepted);
    }

    [Fact]
    public void TheByteBucketLimitsLongLinesToAboutSixteenKilobytesPerSecond()
    {
        var store = new NodeDiagnosticsStore(new NodeDiagnosticsOptions { ByteBurst = 16 * 1024, BytesPerSecond = 16 * 1024 });
        var big = Enumerable.Range(0, 40).Select(_ => (LogLevel.Info, new string('x', 1000))).ToList();

        var result = store.Ingest(1, "A", big, T0);

        Assert.Equal(16, result.Accepted.Count);
        Assert.Equal(24, result.Dropped);
    }

    [Fact]
    public void OnlyTheLastNLinesAreKept()
    {
        var store = new NodeDiagnosticsStore(new NodeDiagnosticsOptions { KeepLines = 5, LineBurst = 1000, LinesPerSecond = 1000 });
        store.Ingest(1, "A", Many(8), T0);

        Assert.Equal(["line 3", "line 4", "line 5", "line 6", "line 7"], store.Get(1)!.Lines.Select(l => l.Text));
    }

    [Fact]
    public void LongLinesAreCutAndControlCharactersBecomeSpaces()
    {
        var store = new NodeDiagnosticsStore(new NodeDiagnosticsOptions { MaxLineLength = 20 });
        store.Ingest(1, "A", Lines(new string('z', 50), "a\r\nb\tc"), T0);

        var lines = store.Get(1)!.Lines;
        Assert.Equal(20, lines[0].Text.Length);
        Assert.Equal("a  b c", lines[1].Text);
    }

    private static (LogLevel, string)[] Run(params string[] checks) => [.. checks.Select(c => (LogLevel.Info, "SELFTEST PASS  " + c + "  ok"))];

    [Fact]
    public void RowsBuildTheTableAtOnceAndAWholeBurstIsOneTable()
    {
        var store = new NodeDiagnosticsStore();
        int changes = 0;
        store.Changed += _ => changes++;

        store.Ingest(7, "A", [(LogLevel.Info, "SELFTEST begin (trigger=config)"), .. Run("a", "b"), (LogLevel.Error, "SELFTEST FAIL  c  broken")], T0);
        var table = store.Get(7)!.SelfTest!;
        Assert.Equal(["a", "b", "c"], table.Rows.Select(r => r.Name));
        Assert.Equal((2, 1, 0, 0), (table.Passed, table.Failed, table.Warned, table.Skipped));

        store.Ingest(7, "A", [(LogLevel.Info, "SELFTEST summary: 2 PASS, 1 FAIL, 0 WARN, 0 SKIP")], T0);
        Assert.Equal(3, store.Get(7)!.SelfTest!.Rows.Count); // the summary adds no row
        Assert.True(changes >= 2);
    }

    [Fact]
    public void ARepeatedCheckStartsANewTable()
    {
        var store = new NodeDiagnosticsStore();
        store.Ingest(1, "A", Run("a", "b"), T0);
        store.Ingest(1, "A", Run("c"), T0.AddMilliseconds(100)); // continues the burst
        Assert.Equal(["a", "b", "c"], store.Get(1)!.SelfTest!.Rows.Select(r => r.Name));

        store.Ingest(1, "A", Run("a"), T0.AddMilliseconds(200)); // "a" again: a second run
        Assert.Equal(["a"], store.Get(1)!.SelfTest!.Rows.Select(r => r.Name));
    }

    [Fact]
    public void AGapOfTwoSecondsStartsANewTable()
    {
        var store = new NodeDiagnosticsStore();
        store.Ingest(1, "A", Run("a"), T0);
        store.Ingest(1, "A", Run("b"), T0.AddSeconds(1.9));
        Assert.Equal(2, store.Get(1)!.SelfTest!.Rows.Count);

        store.Ingest(1, "A", Run("c"), T0.AddSeconds(4.1));
        Assert.Equal(["c"], store.Get(1)!.SelfTest!.Rows.Select(r => r.Name));
    }

    [Fact]
    public void ASummaryOrBeginClosesTheRunSoTheNextRowStartsFresh()
    {
        var store = new NodeDiagnosticsStore();
        store.Ingest(1, "A", [.. Run("a"), (LogLevel.Info, "SELFTEST summary: 1 PASS")], T0);
        store.Ingest(1, "A", Run("b"), T0.AddMilliseconds(10));
        Assert.Equal(["b"], store.Get(1)!.SelfTest!.Rows.Select(r => r.Name));

        store.Ingest(1, "A", [(LogLevel.Info, "SELFTEST begin"), .. Run("c")], T0.AddMilliseconds(20));
        Assert.Equal(["c"], store.Get(1)!.SelfTest!.Rows.Select(r => r.Name));
    }

    [Fact]
    public void LinesThatAreNotRowsChangeNoTable()
    {
        var store = new NodeDiagnosticsStore();
        store.Ingest(1, "A", [(LogLevel.Info, "SELFTEST garbage here"), (LogLevel.Info, "SELFTEST summary: nothing")], T0);
        Assert.Null(store.Get(1)!.SelfTest);
    }

    [Fact]
    public void SelfTestLinesAreNotSubjectToTheLineLimit()
    {
        var store = new NodeDiagnosticsStore();
        store.Ingest(1, "A", Many(100), T0); // bucket empty: ordinary lines are refused now

        var result = store.Ingest(1, "A", Run([.. Enumerable.Range(0, 60).Select(i => "check" + i)]), T0);

        Assert.Equal(0, result.Dropped);
        Assert.Equal(60, store.Get(1)!.SelfTest!.Rows.Count);
    }

    [Fact]
    public void ATableHasAtMostTwoHundredRows()
    {
        var store = new NodeDiagnosticsStore();
        var rows = Enumerable.Range(0, 260).Select(i => (LogLevel.Info, $"SELFTEST PASS c{i} ok")).ToList();
        store.Ingest(1, "A", rows, T0);

        Assert.Equal(SelfTestParser.MaxRows, store.Get(1)!.SelfTest!.Rows.Count);
    }

    [Fact]
    public void ChangedFiresOnlyWhenSomethingWasKept()
    {
        var store = new NodeDiagnosticsStore();
        var ids = new List<int>();
        store.Changed += ids.Add;

        store.Ingest(3, "A", [], T0);
        Assert.Empty(ids);
        store.Ingest(3, "A", Lines("hello"), T0);
        Assert.Equal([3], ids);
        store.Ingest(3, "A", Many(500), T0); // the bucket is nearly empty, but some lines still pass
        Assert.Equal(2, ids.Count);
        store.Ingest(3, "A", Many(5), T0); // all refused
        Assert.Equal(2, ids.Count);
    }

    [Fact]
    public void GetReturnsNullForAnUnknownPlayerAndAllListsEveryNode()
    {
        var store = new NodeDiagnosticsStore();
        Assert.Null(store.Get(9));
        store.Ingest(1, "A", Lines("x"), T0);
        store.Ingest(2, "B", Lines("y"), T0);
        Assert.Equal([1, 2], store.All().Select(s => s.PlayerId).Order());
    }
}

public sealed class TokenBucketTests
{
    [Fact]
    public void SpendMayGoNegativeButNeverBelowMinusBurst()
    {
        var t = new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);
        var bucket = new TokenBucket(10, 20);
        bucket.Spend(1000, t);
        Assert.False(bucket.TryTake(1, t));
        Assert.False(bucket.TryTake(1, t.AddSeconds(1.9))); // -20 + 19 = -1
        Assert.True(bucket.TryTake(1, t.AddSeconds(2.1)));  // +1
    }
}

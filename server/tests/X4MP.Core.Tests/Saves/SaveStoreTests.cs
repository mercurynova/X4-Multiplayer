using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using X4MP.Core.Saves;
using X4MP.Proto;

namespace X4MP.Core.Tests.Saves;

/// <summary>Scratch directory that cleans up after itself.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "x4mp-core-saves-" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // best effort
        }
    }
}

public sealed class SaveSnifferTests
{
    private static string Gzip(TempDir dir, string name, string content, CompressionLevel level = CompressionLevel.Fastest)
    {
        string path = dir.File(name);
        using var file = File.Create(path);
        using var gzip = new GZipStream(file, level);
        gzip.Write(Encoding.UTF8.GetBytes(content));
        return path;
    }

    private const string Head =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<savegame><info><save name=\"quick\" date=\"1700000000\"/><game id=\"x4\" version=\"900\" build=\"611726\"/>" +
        "<player name=\"Jack\" location=\"cluster_01\" money=\"123456700\"/></info><universe>";

    [Fact]
    public void ASavegameIsAcceptedAndItsInfoRead()
    {
        using var dir = new TempDir();
        string path = Gzip(dir, "ok.xml.gz", Head + "</universe></savegame>");
        Assert.Equal(SniffResult.Ok, SaveSniffer.Check(path, UploadKind.Save, out var meta));
        Assert.Equal("900", meta.GameVersion);
        Assert.Equal("1700000000", meta.SaveTime);
        Assert.Equal("Jack", meta.PlayerName);
        Assert.Equal(123456700L, meta.PlayerMoney);
    }

    [Fact]
    public void AByteOrderMarkAndACommentBeforeTheRootAreFine()
    {
        using var dir = new TempDir();
        string path = Gzip(dir, "bom.xml.gz", "﻿<?xml version=\"1.0\"?><!-- made by X4 --><savegame><info/></savegame>");
        Assert.Equal(SniffResult.Ok, SaveSniffer.Check(path, UploadKind.Save, out _));
    }

    [Fact]
    public void OnlyTheFirstQuarterMegabyteIsLookedAtSoABigSaveIsNotUnpacked()
    {
        using var dir = new TempDir();
        string path = Gzip(dir, "big.xml.gz", Head + new string('x', 5 * 1024 * 1024));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(SniffResult.Ok, SaveSniffer.Check(path, UploadKind.Save, out var meta));
        Assert.True(clock.ElapsedMilliseconds < 2000);
        Assert.Equal("Jack", meta.PlayerName);
    }

    [Fact]
    public void PlainTextIsNotGzip()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("plain.xml.gz"), "<savegame></savegame>");
        Assert.Equal(SniffResult.NotGzip, SaveSniffer.Check(dir.File("plain.xml.gz"), UploadKind.Save, out _));
    }

    [Fact]
    public void ABrokenGzipBodyIsNotGzip()
    {
        using var dir = new TempDir();
        File.WriteAllBytes(dir.File("broken.xml.gz"), [0x1F, 0x8B, 0x08, 0x00, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12]);
        Assert.Equal(SniffResult.NotGzip, SaveSniffer.Check(dir.File("broken.xml.gz"), UploadKind.Save, out _));
    }

    [Theory]
    [InlineData("<html><body>nope</body></html>")]
    [InlineData("<savegames/>")]
    [InlineData("not xml at all")]
    public void GzipThatIsNotASavegameIsRejected(string content)
    {
        using var dir = new TempDir();
        string path = Gzip(dir, "other.xml.gz", content);
        Assert.Equal(SniffResult.NotASave, SaveSniffer.Check(path, UploadKind.Save, out _));
    }

    [Fact]
    public void AMissingFileIsNotGzipAndNeverThrows()
    {
        using var dir = new TempDir();
        Assert.Equal(SniffResult.NotGzip, SaveSniffer.Check(dir.File("absent"), UploadKind.Save, out _));
    }

    [Fact]
    public void AManifestNeedsTheX4mfIdentifier()
    {
        using var dir = new TempDir();
        var good = new ManifestT { CheckpointId = new Id128T { Lo = 1, Hi = 2 }, Strings = [], Sectors = [], Entries = [] }.SerializeToBinary();
        File.WriteAllBytes(dir.File("m.x4mf"), good);
        Assert.Equal(SniffResult.Ok, SaveSniffer.Check(dir.File("m.x4mf"), UploadKind.Manifest, out _));

        File.WriteAllBytes(dir.File("bad.x4mf"), [8, 0, 0, 0, (byte)'X', (byte)'X', (byte)'X', (byte)'X', 0, 0]);
        Assert.Equal(SniffResult.NotASave, SaveSniffer.Check(dir.File("bad.x4mf"), UploadKind.Manifest, out _));
        File.WriteAllBytes(dir.File("tiny.x4mf"), [1, 2]);
        Assert.Equal(SniffResult.NotASave, SaveSniffer.Check(dir.File("tiny.x4mf"), UploadKind.Manifest, out _));
    }
}

public sealed class SaveFileStoreTests
{
    private static readonly string Sha = new('a', 64);

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("A" + "AAAA")]
    [InlineData("")]
    public void OnlyLowercaseHexHashesAreAcceptedSoNoFileNameComesFromUserInput(string bad)
    {
        Assert.False(SaveFileStore.IsValidSha(bad));
        using var dir = new TempDir();
        var store = new SaveFileStore(dir.Path);
        Assert.Throws<ArgumentException>(() => store.PathOf(bad, UploadKind.Save));
        Assert.False(store.Exists(bad, UploadKind.Save));
        Assert.Null(store.Find(bad));
    }

    [Fact]
    public void PathsAreContentAddressedAndPromoteIsAtomicAndIdempotent()
    {
        using var dir = new TempDir();
        var store = new SaveFileStore(dir.Path);
        Assert.Equal(Path.Combine(dir.Path, "saves", Sha + ".xml.gz"), store.PathOf(Sha, UploadKind.Save));
        Assert.Equal(Path.Combine(dir.Path, "saves", Sha + ".x4mf"), store.PathOf(Sha, UploadKind.Manifest));
        Assert.Equal("x4mp_" + Sha[..12] + ".xml.gz", SaveFileStore.LocalFileName(Sha));

        string part = store.PartPathOf(Sha, UploadKind.Save);
        File.WriteAllBytes(part, [1, 2, 3]);
        store.Promote(part, Sha, UploadKind.Save);
        Assert.False(File.Exists(part));
        Assert.Equal(3, store.SizeOf(Sha, UploadKind.Save));
        Assert.Equal(UploadKind.Save, store.Find(Sha)!.Kind);

        // the same content arriving again: the stored file wins, the part is dropped
        File.WriteAllBytes(part, [9, 9, 9, 9]);
        store.Promote(part, Sha, UploadKind.Save);
        Assert.False(File.Exists(part));
        Assert.Equal(3, store.SizeOf(Sha, UploadKind.Save));

        Assert.Single(store.Enumerate());
        Assert.True(store.Delete(Sha, UploadKind.Save));
        Assert.False(store.Delete(Sha, UploadKind.Save));
        Assert.Empty(store.Enumerate());
    }
}

public sealed class DownloadTokenTests
{
    [Fact]
    public void ATokenIsRandomBoundToItsPlayerAndExpires()
    {
        var time = new FakeTimeProvider();
        var tokens = new DownloadTokens(time);
        string a = tokens.Issue(7, TimeSpan.FromHours(1));
        string b = tokens.Issue(8, TimeSpan.FromHours(1));
        Assert.NotEqual(a, b);
        Assert.Equal(32, a.Length);
        Assert.True(tokens.TryValidate(a, out int player));
        Assert.Equal(7, player);
        Assert.False(tokens.TryValidate("nope", out _));
        Assert.False(tokens.TryValidate(null, out _));

        time.Advance(TimeSpan.FromMinutes(59));
        Assert.True(tokens.TryValidate(a, out _));
        time.Advance(TimeSpan.FromMinutes(2));
        Assert.False(tokens.TryValidate(a, out _));
    }

    [Fact]
    public void RevokingAPlayerKillsItsTokensOnly()
    {
        var tokens = new DownloadTokens(TimeProvider.System);
        string mine = tokens.Issue(1, TimeSpan.FromHours(1));
        string other = tokens.Issue(2, TimeSpan.FromHours(1));
        tokens.RevokePlayer(1);
        Assert.False(tokens.TryValidate(mine, out _));
        Assert.True(tokens.TryValidate(other, out _));
    }
}

public sealed class BandwidthLimiterTests
{
    [Fact]
    public async Task ZeroMeansUnlimited()
    {
        var limiter = new BandwidthLimiter(() => 0, new FakeTimeProvider());
        for (int i = 0; i < 100; i++)
        {
            await limiter.AcquireAsync(10_000_000, CancellationToken.None);
        }
    }

    [Fact]
    public async Task TheCapSpacesOutBytesLeavingABurstAndSharesOneBucket()
    {
        var time = new FakeTimeProvider();
        var limiter = new BandwidthLimiter(() => 1.0, time); // 1 MiB/s
        const int mib = 1024 * 1024;

        await limiter.AcquireAsync(mib, CancellationToken.None); // the burst
        var second = limiter.AcquireAsync(mib, CancellationToken.None).AsTask();
        var third = limiter.AcquireAsync(mib, CancellationToken.None).AsTask(); // another producer of the same bucket
        await Task.Delay(50);
        Assert.False(second.IsCompleted);

        time.Advance(TimeSpan.FromSeconds(1.01));
        await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(third.IsCompleted); // it needs a second more
        time.Advance(TimeSpan.FromSeconds(1.01));
        await third.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AWaitingSenderIsCancelled()
    {
        var time = new FakeTimeProvider();
        var limiter = new BandwidthLimiter(() => 1.0, time);
        const int mib = 1024 * 1024;
        await limiter.AcquireAsync(mib, CancellationToken.None);
        using var cts = new CancellationTokenSource();
        var waiting = limiter.AcquireAsync(mib, cts.Token).AsTask();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }
}

public sealed class SaveJanitorTests
{
    private static string Sha(char c) => new(c, 64);

    private static SaveRecord Record(char c, DateTimeOffset at, bool pinned = false) =>
        new(Sha(c), 10, "save " + c, "authority", null, at, SaveMeta.Empty, true, pinned);

    private static CheckpointRecord Checkpoint(char save, char manifest) =>
        new(new X4MP.Core.World.CheckpointId(1, 2), Sha(save), Sha(manifest), 5, 1, 0, 0, true, DateTimeOffset.UtcNow);

    [Fact]
    public void KeepsThePinnedTheNewestNTheReferencedAndTheLiveOnesAndDeletesTheRestWithTheirManifests()
    {
        using var dir = new TempDir();
        var files = new SaveFileStore(dir.Path);
        var catalog = new InMemorySaveCatalog();
        var t0 = DateTimeOffset.UtcNow.AddDays(-10);

        // a (oldest), b pinned, c referenced by a session, d protected by the live session, e and f the newest two
        foreach (var (c, i) in "abcdef".Select((c, i) => (c, i)))
        {
            catalog.AddSave(Record(c, t0.AddHours(i), pinned: c == 'b'));
            File.WriteAllBytes(files.PathOf(Sha(c), UploadKind.Save), [1, 2, 3]);
        }

        // 'a' has a manifest that goes with it
        catalog.AddCheckpoint(1, Checkpoint('a', '7'));
        File.WriteAllBytes(files.PathOf(Sha('7'), UploadKind.Manifest), [4, 5]);
        catalog.SetSessionSave(5, Sha('c'), initial: false);

        var janitor = new SaveJanitor(
            files, catalog, () => new SaveOptions { SaveRetentionCount = 2 }, () => new HashSet<string> { Sha('d') }, _ => false);
        var report = janitor.Run();

        Assert.Equal(1, report.DeletedSaves);
        Assert.Equal(1, report.DeletedManifests);
        Assert.False(files.Exists(Sha('a'), UploadKind.Save));
        Assert.False(files.Exists(Sha('7'), UploadKind.Manifest));
        Assert.Null(catalog.Find(Sha('a')));
        foreach (char kept in "bcdef")
        {
            Assert.True(files.Exists(Sha(kept), UploadKind.Save), $"{kept} is kept");
            Assert.NotNull(catalog.Find(Sha(kept)));
        }
    }

    [Fact]
    public void RemovesOldPartialUploadsButNotFreshOnesOrOnesInUse()
    {
        using var dir = new TempDir();
        var files = new SaveFileStore(dir.Path);
        string old = files.PartPathOf(Sha('1'), UploadKind.Save);
        string fresh = files.PartPathOf(Sha('2'), UploadKind.Save);
        string busy = files.PartPathOf(Sha('3'), UploadKind.Save);
        foreach (var p in new[] { old, fresh, busy })
        {
            File.WriteAllBytes(p, [1]);
        }

        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-30));
        File.SetLastWriteTimeUtc(busy, DateTime.UtcNow.AddHours(-30));

        var janitor = new SaveJanitor(files, new InMemorySaveCatalog(), () => new SaveOptions { UploadRetentionHours = 24 }, () => new HashSet<string>(), p => p == busy);
        var report = janitor.Run();

        Assert.Equal(1, report.DeletedParts);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(fresh));
        Assert.True(File.Exists(busy));
    }

    [Fact]
    public void AnOldFileThatNoRowKnowsIsRemovedButARecentOneWaits()
    {
        using var dir = new TempDir();
        var files = new SaveFileStore(dir.Path);
        string orphan = files.PathOf(Sha('9'), UploadKind.Save);
        string recent = files.PathOf(Sha('8'), UploadKind.Save);
        File.WriteAllBytes(orphan, [1]);
        File.WriteAllBytes(recent, [1]);
        File.SetLastWriteTimeUtc(orphan, DateTime.UtcNow.AddDays(-3));

        var report = new SaveJanitor(files, new InMemorySaveCatalog(), () => new SaveOptions(), () => new HashSet<string>(), _ => false).Run();

        Assert.Equal(1, report.DeletedOrphans);
        Assert.False(File.Exists(orphan));
        Assert.True(File.Exists(recent));
    }
}

public sealed class InMemorySaveCatalogTests
{
    [Fact]
    public void DeleteReturnsTheManifestsOfItsCheckpointsAndForgetsThem()
    {
        var catalog = new InMemorySaveCatalog();
        var save = new SaveRecord(new string('a', 64), 1, "x", "authority", null, DateTimeOffset.UtcNow, SaveMeta.Empty, true);
        catalog.AddSave(save);
        catalog.AddSave(save with { DisplayName = "second add keeps the first" });
        Assert.Equal("x", catalog.Find(save.Sha256)!.DisplayName);
        catalog.AddCheckpoint(1, new CheckpointRecord(new X4MP.Core.World.CheckpointId(1, 1), save.Sha256, new string('b', 64), 2, 3, 0, 0, true, DateTimeOffset.UtcNow));

        Assert.Contains(new string('b', 64), catalog.ManifestSha256());
        Assert.True(catalog.Update(save.Sha256, "renamed", true));
        Assert.True(catalog.Find(save.Sha256)!.Pinned);
        Assert.False(catalog.Update(new string('z', 64), "x", null));
        Assert.Equal([new string('b', 64)], catalog.Delete(save.Sha256));
        Assert.Empty(catalog.ManifestSha256());
        Assert.Empty(catalog.Delete(save.Sha256));
    }
}

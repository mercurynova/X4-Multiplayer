using System.Text;
using X4MP.Launcher.Core;

namespace X4MP.Launcher.Core.Tests;

public sealed class ProfileWriterTests
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 2, 9, 30, 0, TimeSpan.Zero);

    private const string Profile =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
        "<content>\r\n" +
        "  <extension id=\"a\" enabled=\"false\"></extension>\r\n" +
        "  <extension id=\"b\" enabled=\"true\"></extension>\r\n" +
        "</content>\r\n";

    [Fact]
    public void ApplyThenRestoreLeavesTheFileByteForByteIdentical()
    {
        using var tree = new TempTree();
        var profile = ProfileAt(tree, Profile);
        string before = File.ReadAllText(profile.ContentXmlPath);

        ApplyResult applied = ProfileWriter.Apply(profile, "Friday Run", [Toggle(PlanAction.Enable, "a"), Toggle(PlanAction.Disable, "b")], gameRunning: false, Noon);

        Assert.True(applied.Changed);
        Assert.Equal(2, applied.Edits);
        Assert.NotNull(applied.BackupFile);
        Assert.True(File.Exists(applied.BackupFile));
        Assert.Equal(before, File.ReadAllText(applied.BackupFile!));
        string changed = File.ReadAllText(profile.ContentXmlPath);
        Assert.Contains("<extension id=\"a\" enabled=\"true\">", changed, StringComparison.Ordinal);
        Assert.Contains("<extension id=\"b\" enabled=\"false\">", changed, StringComparison.Ordinal);
        Assert.True(File.Exists(ProfileWriter.JournalPath(profile)));

        RestoreResult restored = ProfileWriter.Restore(profile);

        Assert.True(restored.Restored);
        Assert.Empty(restored.Skipped);
        Assert.False(restored.FileRemoved);
        Assert.Equal(before, File.ReadAllText(profile.ContentXmlPath));
        Assert.False(File.Exists(ProfileWriter.JournalPath(profile)));
    }

    [Fact]
    public void ApplyRefusesWhileTheGameRunsAndTouchesNothing()
    {
        using var tree = new TempTree();
        var profile = ProfileAt(tree, Profile);
        string before = File.ReadAllText(profile.ContentXmlPath);

        Assert.Throws<InvalidOperationException>(() =>
            ProfileWriter.Apply(profile, "Friday Run", [Toggle(PlanAction.Enable, "a")], gameRunning: true, Noon));

        Assert.Equal(before, File.ReadAllText(profile.ContentXmlPath));
        Assert.False(Directory.Exists(ProfileWriter.BackupsDirectory(profile)));
    }

    [Fact]
    public void RestoreLeavesAnEntryAloneWhenThePlayerChangedItInGame()
    {
        using var tree = new TempTree();
        var profile = ProfileAt(tree, Profile);
        ProfileWriter.Apply(profile, "Friday Run", [Toggle(PlanAction.Enable, "a"), Toggle(PlanAction.Disable, "b")], gameRunning: false, Noon);

        // The player opened Extensions and switched "a" back off while the launcher was closed.
        string current = File.ReadAllText(profile.ContentXmlPath);
        File.WriteAllText(profile.ContentXmlPath, current.Replace("id=\"a\" enabled=\"true\"", "id=\"a\" enabled=\"false\"", StringComparison.Ordinal));

        RestoreResult restored = ProfileWriter.Restore(profile);

        Assert.True(restored.Restored);
        Assert.Equal(["a"], restored.Skipped);
        string text = File.ReadAllText(profile.ContentXmlPath);
        Assert.Contains("<extension id=\"a\" enabled=\"false\">", text, StringComparison.Ordinal);
        Assert.Contains("<extension id=\"b\" enabled=\"true\">", text, StringComparison.Ordinal);
        Assert.False(File.Exists(ProfileWriter.JournalPath(profile)));
    }

    [Fact]
    public void AProfileWithoutContentXmlIsCreatedAndRemovedAgain()
    {
        using var tree = new TempTree();
        var profile = new X4Profile(tree.Dir("profile"), "123456789");
        Assert.False(File.Exists(profile.ContentXmlPath));

        ApplyResult applied = ProfileWriter.Apply(profile, "Friday Run", [Toggle(PlanAction.Enable, "new_mod")], gameRunning: false, Noon);

        Assert.True(applied.Changed);
        Assert.Null(applied.BackupFile);
        Assert.True(File.Exists(profile.ContentXmlPath));
        string text = File.ReadAllText(profile.ContentXmlPath);
        Assert.Contains("<extension id=\"new_mod\" enabled=\"true\"></extension>", text, StringComparison.Ordinal);

        RestoreResult restored = ProfileWriter.Restore(profile);

        Assert.True(restored.Restored);
        Assert.True(restored.FileRemoved);
        Assert.False(File.Exists(profile.ContentXmlPath));
        Assert.False(File.Exists(ProfileWriter.JournalPath(profile)));
    }

    [Fact]
    public void OnlyTheTenNewestBackupsAreKept()
    {
        using var tree = new TempTree();
        var profile = ProfileAt(tree, Profile);
        string backups = ProfileWriter.BackupsDirectory(profile);
        Directory.CreateDirectory(backups);
        for (int i = 0; i < 12; i++)
        {
            File.WriteAllText(Path.Combine(backups, "content.20260101-0000" + i.ToString("00", System.Globalization.CultureInfo.InvariantCulture) + ".xml"), "old");
        }

        ApplyResult applied = ProfileWriter.Apply(profile, "Friday Run", [Toggle(PlanAction.Enable, "a")], gameRunning: false, Noon);

        string[] kept = Directory.GetFiles(backups, "content.*.xml");
        Assert.Equal(10, kept.Length);
        Assert.Contains(applied.BackupFile!, kept, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.Combine(backups, "content.20260101-000000.xml"), kept, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.Combine(backups, "content.20260101-000002.xml"), kept, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void APlanWithoutTogglesWritesNothing()
    {
        using var tree = new TempTree();
        var profile = ProfileAt(tree, Profile);
        string before = File.ReadAllText(profile.ContentXmlPath);

        ApplyResult applied = ProfileWriter.Apply(
            profile,
            "Friday Run",
            [
                new PlanItem(PlanAction.Missing, "gone", "Gone", "Sim", "Required", "1.00", null, 0, null, "", false, false),
                new PlanItem(PlanAction.Stale, "stale", "stale", "Unknown", "", "", null, 0, null, "", false, false),
            ],
            gameRunning: false,
            Noon);

        Assert.False(applied.Changed);
        Assert.Null(applied.BackupFile);
        Assert.Null(applied.JournalFile);
        Assert.Equal(before, File.ReadAllText(profile.ContentXmlPath));
        Assert.False(Directory.Exists(ProfileWriter.BackupsDirectory(profile)));
    }

    [Fact]
    public void RestoreSkipsAnAddedEntryThePlayerRemoved()
    {
        using var tree = new TempTree();
        var profile = ProfileAt(tree, Profile);
        ProfileWriter.Apply(profile, "Friday Run", [Toggle(PlanAction.Enable, "added")], gameRunning: false, Noon);
        string current = File.ReadAllText(profile.ContentXmlPath);
        File.WriteAllText(profile.ContentXmlPath, current.Replace("  <extension id=\"added\" enabled=\"true\"></extension>\n", string.Empty, StringComparison.Ordinal));

        RestoreResult restored = ProfileWriter.Restore(profile);

        Assert.True(restored.Restored);
        Assert.Equal(["added"], restored.Skipped);
        Assert.Equal(Profile, File.ReadAllText(profile.ContentXmlPath));
    }

    [Fact]
    public void BomIsPreservedByApplyAndRestore()
    {
        using var tree = new TempTree();
        string directory = tree.Dir("profile");
        var profile = new X4Profile(directory, "123456789");
        byte[] original = [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(Profile)];
        File.WriteAllBytes(profile.ContentXmlPath, original);

        ProfileWriter.Apply(profile, "Friday Run", [Toggle(PlanAction.Enable, "a")], gameRunning: false, Noon);
        byte[] changed = File.ReadAllBytes(profile.ContentXmlPath);
        Assert.Equal(Encoding.UTF8.GetPreamble(), changed[..3]);
        Assert.Contains("enabled=\"true\"", Encoding.UTF8.GetString(changed, 3, changed.Length - 3), StringComparison.Ordinal);

        ProfileWriter.Restore(profile);
        Assert.Equal(original, File.ReadAllBytes(profile.ContentXmlPath));
    }

    [Fact]
    public void DiscardForgetsTheJournalAndLeavesTheNewValues()
    {
        using var tree = new TempTree();
        var profile = ProfileAt(tree, Profile);
        ProfileWriter.Apply(profile, "Friday Run", [Toggle(PlanAction.Enable, "a")], gameRunning: false, Noon);
        Assert.True(ProfileWriter.HasPendingRestore(profile));

        Assert.True(ProfileWriter.Discard(profile));

        Assert.False(ProfileWriter.HasPendingRestore(profile));
        Assert.Contains("id=\"a\" enabled=\"true\"", File.ReadAllText(profile.ContentXmlPath), StringComparison.Ordinal);
        RestoreResult restored = ProfileWriter.Restore(profile);
        Assert.False(restored.Restored);
    }

    private static X4Profile ProfileAt(TempTree tree, string content)
    {
        string directory = tree.Dir("profile");
        File.WriteAllText(Path.Combine(directory, "content.xml"), content);
        return new X4Profile(directory, "123456789");
    }

    private static PlanItem Toggle(PlanAction action, string id) =>
        new(action, id, id, "Sim", "Required", "1.00", "1.00", 0, null, "", false, false);
}

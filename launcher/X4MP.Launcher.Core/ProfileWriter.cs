using System.Globalization;
using System.Text;
using System.Text.Json;

namespace X4MP.Launcher.Core;

/// <summary>
/// What <see cref="ProfileWriter.Apply"/> did, in the terms the window reports: nothing when the plan asked for no
/// toggle, otherwise the backup it took and the journal it left behind for <see cref="ProfileWriter.Restore"/>.
/// </summary>
public sealed record ApplyResult(bool Changed, string ContentXmlPath, string? BackupFile, string? JournalFile, int Edits);

/// <summary>Outcome of a restore: entries written back, entries left alone because they no longer hold our value.</summary>
public sealed record RestoreResult(bool Restored, IReadOnlyList<string> Skipped, bool FileRemoved);

/// <summary>
/// The journal written next to the backups while the launcher's values are in the profile. It records the exact
/// original text of every element it touched, which is what makes a restore byte-exact.
/// </summary>
public sealed record RestoreJournal(
    int Version,
    string Server,
    DateTimeOffset CreatedUtc,
    string File,
    bool FileExisted,
    bool HadBom,
    IReadOnlyList<ProfileTagEdit> Edits);

/// <summary>
/// Writes the plan into a profile's content.xml and puts the file back afterwards (M6-X3, mod-management 4.2
/// steps 5-8). Only the extension elements the plan names are touched; every other byte of the file, the line
/// endings and the BOM included, is left as it was, so apply-then-restore returns the identical file.
/// </summary>
public static class ProfileWriter
{
    /// <summary>The document used when a profile has no content.xml yet.</summary>
    public const string MinimalDocument = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<content>\n</content>\n";

    private const string JournalName = "pending-restore.json";
    private const int BackupsToKeep = 10;
    private const int WriteAttempts = 5;
    private const int WriteRetryMilliseconds = 100;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    private static readonly JsonSerializerOptions JournalJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    /// <summary>Where the profile's backups and its journal live: a folder beside content.xml, not in the game.</summary>
    public static string BackupsDirectory(X4Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return Path.Combine(profile.Directory, "x4mp", "profile-backups");
    }

    public static string JournalPath(X4Profile profile) => Path.Combine(BackupsDirectory(profile), JournalName);

    /// <summary>True when a previous session left values in the profile that still have to be put back.</summary>
    public static bool HasPendingRestore(X4Profile profile) => File.Exists(JournalPath(profile));

    /// <summary>
    /// Writes the toggles of <paramref name="plan"/> into <c>content.xml</c>. Throws when the game runs, because the
    /// game writes the same file and would either keep its own copy or overwrite ours a minute later.
    /// </summary>
    public static ApplyResult Apply(
        X4Profile profile, string server, IReadOnlyList<PlanItem> plan, bool gameRunning, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(plan);
        if (gameRunning)
        {
            throw new InvalidOperationException("Close X4 before changing the mod set: the game writes content.xml itself while it runs.");
        }

        string path = profile.ContentXmlPath;
        bool existed = File.Exists(path);
        bool hadBom = false;
        string original = MinimalDocument;
        if (existed)
        {
            original = ReadText(path, out hadBom);
        }

        (string updated, IReadOnlyList<ProfileTagEdit> edits) = ProfileContentEditor.Apply(original, plan);
        if (edits.Count == 0)
        {
            return new ApplyResult(false, path, null, null, 0);
        }

        string backups = BackupsDirectory(profile);
        Directory.CreateDirectory(backups);

        string? backupFile = null;
        if (existed)
        {
            backupFile = UniqueBackupPath(backups, now);
            File.Copy(path, backupFile, overwrite: false);
            PruneBackups(backups);
        }

        string journalFile = Path.Combine(backups, JournalName);
        var journal = new RestoreJournal(1, server, now, path, existed, hadBom, edits);
        File.WriteAllText(journalFile, JsonSerializer.Serialize(journal, JournalJson), Utf8NoBom);

        try
        {
            WriteAtomic(path, updated, hadBom);
        }
        catch
        {
            // The journal describes a file this run never changed; a later start must not restore over the player.
            File.Delete(journalFile);
            throw;
        }

        return new ApplyResult(true, path, backupFile, journalFile, edits.Count);
    }

    /// <summary>
    /// Puts back what <see cref="Apply"/> changed, but only for the entries that still hold the value this launcher
    /// wrote: if the player flipped one in the game, that choice wins and is reported in <see cref="RestoreResult.Skipped"/>.
    /// </summary>
    public static RestoreResult Restore(X4Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        string journalFile = JournalPath(profile);
        if (!File.Exists(journalFile))
        {
            return new RestoreResult(false, [], false);
        }

        RestoreJournal? journal;
        try
        {
            journal = JsonSerializer.Deserialize<RestoreJournal>(File.ReadAllText(journalFile, Utf8NoBom), JournalJson);
        }
        catch (JsonException)
        {
            // An unreadable journal cannot be acted on. Keep it: it is the only record of what was changed.
            return new RestoreResult(false, [], false);
        }

        if (journal is null || journal.Edits.Count == 0)
        {
            File.Delete(journalFile);
            return new RestoreResult(false, [], false);
        }

        string path = string.IsNullOrEmpty(journal.File) ? profile.ContentXmlPath : journal.File;
        if (!File.Exists(path))
        {
            // The player removed the file the launcher created; there is nothing to put back.
            File.Delete(journalFile);
            return new RestoreResult(false, [], false);
        }

        string current = ReadText(path, out _);
        (string restored, IReadOnlyList<string> skipped) = ProfileContentEditor.Restore(current, journal.Edits);

        bool removeFile = !journal.FileExisted && restored == MinimalDocument;
        if (removeFile)
        {
            File.Delete(path);
        }
        else
        {
            WriteAtomic(path, restored, journal.HadBom);
        }

        File.Delete(journalFile);
        return new RestoreResult(true, skipped, removeFile);
    }

    /// <summary>Forgets the journal without touching the profile: the player ticked "keep this mod set".</summary>
    public static bool Discard(X4Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        string journalFile = JournalPath(profile);
        if (!File.Exists(journalFile))
        {
            return false;
        }

        File.Delete(journalFile);
        return true;
    }

    private static string UniqueBackupPath(string directory, DateTimeOffset now)
    {
        string stamp = now.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string candidate = Path.Combine(directory, "content." + stamp + ".xml");
        int suffix = 1;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(directory, "content." + stamp + "-" + suffix.ToString(CultureInfo.InvariantCulture) + ".xml");
            suffix++;
        }

        return candidate;
    }

    private static void PruneBackups(string directory)
    {
        string[] files = Directory.GetFiles(directory, "content.*.xml");
        if (files.Length <= BackupsToKeep)
        {
            return;
        }

        Array.Sort(files, StringComparer.Ordinal);
        for (int i = 0; i < files.Length - BackupsToKeep; i++)
        {
            File.Delete(files[i]);
        }
    }

    private static string ReadText(string path, out bool hadBom)
    {
        byte[] bytes = File.ReadAllBytes(path);
        hadBom = bytes.Length >= Utf8Bom.Length && bytes[0] == Utf8Bom[0] && bytes[1] == Utf8Bom[1] && bytes[2] == Utf8Bom[2];
        return hadBom ? Encoding.UTF8.GetString(bytes, Utf8Bom.Length, bytes.Length - Utf8Bom.Length) : Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// Writes a sibling temp file and renames it over the target, retrying briefly: an OneDrive or antivirus handle
    /// on content.xml makes the replace fail for a moment, not for good.
    /// </summary>
    private static void WriteAtomic(string path, string text, bool bom)
    {
        string directory = Path.GetDirectoryName(path) ?? ".";
        string temp = Path.Combine(directory, Path.GetFileName(path) + ".x4mp-tmp");
        File.WriteAllText(temp, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: bom));

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < WriteAttempts)
            {
                Thread.Sleep(WriteRetryMilliseconds);
            }
            catch (UnauthorizedAccessException) when (attempt < WriteAttempts)
            {
                Thread.Sleep(WriteRetryMilliseconds);
            }
            catch
            {
                TryDelete(temp);
                throw;
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

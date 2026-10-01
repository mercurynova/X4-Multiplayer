using X4MP.Core.Settings;

namespace X4MP.Core.Saves;

/// <summary>What a joining player gets as its save (protocol.md 6.5).</summary>
public enum JoinCheckpointPolicy
{
    /// <summary>The newest checkpoint plus the journal since it (no hitch on the authority). Default.</summary>
    LatestPlusJournal,

    /// <summary>When the journal is longer than <see cref="SaveOptions.FreshSaveJournalEntries"/> or the checkpoint is older than <see cref="SaveOptions.FreshSaveMaxAgeMinutes"/>, ask the authority for a fresh save first.</summary>
    FreshSave,
}

/// <summary>
/// Save service settings (server-design 3, protocol.md 6.3 to 6.5, 8.4). Everything the save service reads on every use is Live.
/// </summary>
[SettingsSection(SectionName, "Saves")]
public sealed record SaveOptions
{
    public const string SectionName = "X4MP:Saves";

    /// <summary>Chunk size of the in-band transfers and the largest value the protocol allows.</summary>
    public const int MaxChunkBytes = 256 * 1024;

    [Setting("Ask the authority for a checkpoint every N minutes (0 = off; fractions allowed)", Scope = SettingScope.Live, Min = 0, Max = 240)]
    public double AutosaveMinutes { get; set; } = 15;

    [Setting("What a joining player gets: LatestPlusJournal, or FreshSave (a new checkpoint when the journal or the checkpoint is old)", Scope = SettingScope.Live)]
    public JoinCheckpointPolicy JoinCheckpointPolicy { get; set; } = JoinCheckpointPolicy.LatestPlusJournal;

    [Setting("FreshSave: journal entries since the checkpoint that trigger a new save", Scope = SettingScope.Live, Min = 0, Max = 10_000_000)]
    public int FreshSaveJournalEntries { get; set; } = 5000;

    [Setting("FreshSave: checkpoint age in minutes that triggers a new save", Scope = SettingScope.Live, Min = 0, Max = 1440)]
    public int FreshSaveMaxAgeMinutes { get; set; } = 30;

    [Setting("Longest the server waits for the authority to answer a save request (s)", Scope = SettingScope.Live, Min = 5, Max = 3600)]
    public int SaveRequestTimeoutSeconds { get; set; } = 300;

    [Setting("Largest accepted save or manifest (bytes)", Min = 1024)]
    public long MaxSaveBytes { get; set; } = 1L << 30;

    [Setting("Outbound save bandwidth cap, all downloads together (MB/s, 0 = unlimited)", Scope = SettingScope.Live, Min = 0, Max = 100_000)]
    public double SaveBandwidthCapMBps { get; set; }

    [Setting("A transfer whose window stays full without an ack for this long is dropped (s)", Scope = SettingScope.Live, Min = 1, Max = 3600)]
    public int TransferStallSeconds { get; set; } = 45;

    [Setting("Chunk size of in-band transfers (bytes, at most 262144)", Min = 1024, Max = MaxChunkBytes)]
    public int ChunkBytes { get; set; } = MaxChunkBytes;

    [Setting("Chunks in flight before the sender waits for an ack", Min = 1, Max = 64)]
    public int WindowChunks { get; set; } = 8;

    [Setting("Offer the HTTP download fallback (needs the SaveHttp capability on the node)", Scope = SettingScope.Live)]
    public bool HttpFallback { get; set; } = true;

    [Setting("Public base URL of the HTTP fallback sent to nodes (empty = a path the node resolves against the host it connected to)", Scope = SettingScope.Live, MaxLength = 200)]
    public string PublicHttpBaseUrl { get; set; } = string.Empty;

    [Setting("Lifetime of a node's HTTP download token (minutes)", Scope = SettingScope.Live, Min = 1, Max = 1440)]
    public int DownloadTokenMinutes { get; set; } = 60;

    [Setting("Manifest report: above this percentage of unmatched entities the node is refused (ManifestMismatch)", Scope = SettingScope.Live, Min = 0, Max = 100)]
    public double ManifestMismatchPercent { get; set; } = 0.5;

    [Setting("Unpinned saves kept by the janitor (the newest N), besides pinned and referenced ones", Scope = SettingScope.Live, Min = 1, Max = 10_000)]
    public int SaveRetentionCount { get; set; } = 10;

    [Setting("Partial uploads older than this are deleted (hours)", Scope = SettingScope.Live, Min = 1, Max = 8760)]
    public int UploadRetentionHours { get; set; } = 24;

    [Setting("How often the janitor runs (minutes)", Min = 1, Max = 10_080)]
    public int JanitorIntervalMinutes { get; set; } = 60;
}

namespace X4MP.Persistence;

/// <summary>
/// Persistence settings. M0-11 will replace the data-dir default with full data-dir resolution;
/// only <see cref="DataDir"/> needs to change when it does.
/// </summary>
public sealed class PersistenceOptions
{
    /// <summary>Server data directory. Placeholder until M0-11 resolves it properly.</summary>
    public string DataDir { get; set; } = "./data";

    public string DatabaseFileName { get; set; } = "x4mp.db";

    /// <summary>Full path of the SQLite database file (<c>&lt;data-dir&gt;/x4mp.db</c>).</summary>
    public string DatabasePath => Path.GetFullPath(Path.Combine(DataDir, DatabaseFileName));

    /// <summary>SQLite <c>busy_timeout</c> in milliseconds.</summary>
    public int BusyTimeoutMs { get; set; } = 5000;

    /// <summary>Write-behind: flush after this many queued items...</summary>
    public int MaxBatchItems { get; set; } = 500;

    /// <summary>...or after this long since the first queued item of the batch.</summary>
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Bounded queue capacity; producers wait (or <c>TryEnqueue</c> fails) when full.</summary>
    public int QueueCapacity { get; set; } = 100_000;
}

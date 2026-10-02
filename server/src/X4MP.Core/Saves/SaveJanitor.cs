using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using X4MP.Core.Economy;
using X4MP.Proto;

namespace X4MP.Core.Saves;

/// <summary>What one janitor pass removed.</summary>
public sealed record JanitorReport(int DeletedSaves, int DeletedManifests, int DeletedParts, int DeletedOrphans, long FreedBytes);

/// <summary>
/// Keeps the store small (server-design 2.8): deletes partial uploads older than <see cref="SaveOptions.UploadRetentionHours"/>, and
/// unpinned saves beyond the newest <see cref="SaveOptions.SaveRetentionCount"/> that no running session references and that are not the
/// current or previous checkpoint of the live session, together with the manifests of their checkpoints. Files in the store that the
/// catalog does not know and that are older than the upload retention are removed too (a crash between storing and recording).
/// </summary>
public sealed partial class SaveJanitor(
    SaveFileStore files,
    ISaveCatalog catalog,
    Func<SaveOptions> options,
    Func<IReadOnlySet<string>> liveProtected,
    Func<string, bool> isPartInUse,
    TimeProvider? time = null,
    ILogger<SaveJanitor>? logger = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    public JanitorReport Run()
    {
        var opt = options();
        var now = _time.GetUtcNow();
        int parts = 0;
        int saves = 0;
        int manifests = 0;
        int orphans = 0;
        long freed = 0;

        foreach (var part in files.EnumerateParts())
        {
            if (now - part.LastWriteTimeUtc > TimeSpan.FromHours(opt.UploadRetentionHours) && !isPartInUse(part.FullName))
            {
                freed += part.Length;
                SaveFileStore.TryDelete(part.FullName);
                parts++;
            }
        }

        var protectedSet = new HashSet<string>(liveProtected(), StringComparer.Ordinal);
        protectedSet.UnionWith(catalog.ReferencedSha256());
        var all = catalog.List(); // newest first
        var keep = new HashSet<string>(all.Where(s => s.Pinned).Select(s => s.Sha256), StringComparer.Ordinal);
        foreach (var save in all.Where(s => !s.Pinned).Take(opt.SaveRetentionCount))
        {
            keep.Add(save.Sha256);
        }

        foreach (var save in all)
        {
            if (keep.Contains(save.Sha256) || protectedSet.Contains(save.Sha256))
            {
                continue;
            }

            var manifestShas = catalog.Delete(save.Sha256);
            freed += files.SizeOf(save.Sha256, UploadKind.Save) ?? 0;
            if (files.Delete(save.Sha256, UploadKind.Save))
            {
                saves++;
            }

            foreach (var manifest in manifestShas)
            {
                if (!protectedSet.Contains(manifest))
                {
                    freed += files.SizeOf(manifest, UploadKind.Manifest) ?? 0;
                    if (files.Delete(manifest, UploadKind.Manifest))
                    {
                        manifests++;
                    }
                }
            }
        }

        var known = all.Select(s => s.Sha256).ToHashSet(StringComparer.Ordinal);
        var knownManifests = catalog.ManifestSha256();
        foreach (var file in files.Enumerate())
        {
            bool referenced = file.Kind == UploadKind.Save
                ? known.Contains(file.Sha256) && catalog.Find(file.Sha256) is not null
                : knownManifests.Contains(file.Sha256);
            if (!referenced && !protectedSet.Contains(file.Sha256) && now - file.ModifiedAt > TimeSpan.FromHours(opt.UploadRetentionHours))
            {
                freed += file.SizeBytes;
                SaveFileStore.TryDelete(file.Path);
                orphans++;
            }
        }

        var report = new JanitorReport(saves, manifests, parts, orphans, freed);
        if (saves + manifests + parts + orphans > 0)
        {
            LogRemoved(saves, manifests, parts, orphans, freed);
        }

        return report;
    }

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Information, Message = "save janitor removed {Saves} saves, {Manifests} manifests, {Parts} partial uploads, {Orphans} orphans ({Bytes} bytes)")]
    private partial void LogRemoved(int saves, int manifests, int parts, int orphans, long bytes);
}

/// <summary>
/// Seeds the economy from the money stored in the session's first save (ADR-033, <see cref="EconomyService.SeedSaveMoney"/>). The save's
/// <c>money</c> attribute is in whole credits (not cents; only per-transaction amounts and MD player.money are cents, ADR-042), the same unit as the ledger.
/// </summary>
public sealed partial class EconomySaveSeeder(Func<EconomyService?> economy, ILogger<EconomySaveSeeder>? logger = null) : ISaveSeedHook
{
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    public void OnInitialSaveStored(InitialSaveInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (info.Meta.PlayerMoney is not { } credits || credits <= 0 || economy() is not { } service)
        {
            return;
        }

        var result = service.SeedSaveMoney(credits, info.AuthorityPlayerId > 0 ? info.AuthorityPlayerId : null, "system");
        LogSeeded(credits, result.Reason);
    }

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Information, Message = "seeded the economy with {Credits} credits from the save: {Reason}")]
    private partial void LogSeeded(long credits, EconomyReject reason);
}

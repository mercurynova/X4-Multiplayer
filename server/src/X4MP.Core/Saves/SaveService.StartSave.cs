using X4MP.Core.Net;
using X4MP.Core.Session;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.Core.Saves;

public sealed partial class SaveService
{
    /// <summary>The stored save a session is started from (an admin upload or an older checkpoint): what the authority must load before the first checkpoint exists.</summary>
    private sealed record StartSave(string Sha, long Size, string Name, uint NextNetId = 0);

    private StartSave? _startSave;

    // The connection the start save was announced on: a node announced twice on one connection (attach and phase change race) is told once; a resume is a new connection.
    private INodeConnection? _startInfoConnection;

    /// <summary>
    /// Names the stored save the session starts from (<c>POST /sessions {saveId}</c>, null clears it). Until the session has its first checkpoint the
    /// authority is sent this save as a <c>SessionSaveInfo</c> (without a manifest: an uploaded save has none, and the authority needs none) and walks
    /// SyncingSave, Verifying and Loading like a client, unless its <c>ClientHello.loaded_save_sha256</c> already names it. After its <c>NodeReady</c> the
    /// session asks it for the first checkpoint (<c>RequestSave{SessionStart}</c>). False when the file is not in the store or the session already has a checkpoint.
    /// </summary>
    public Task<bool> SetStartSaveAsync(string? sha256) =>
        _driver is null ? Task.FromResult(false) : _driver.CallAsync(() => SetStartSave(sha256));

    private bool SetStartSave(string? sha256)
    {
        if (sha256 is null)
        {
            _startSave = null;
            _startInfoConnection = null;
            PublishStatus();
            return true;
        }

        if (_current is not null)
        {
            return false;
        }

        if (Files.SizeOf(sha256, UploadKind.Save) is not { } size)
        {
            string missing = SaveFileStore.Abbrev(sha256);
            LogStartSaveMissing(missing); // a catalog row without its file (removed by hand): the session carries on as if no save was chosen
            return false;
        }

        string name = Catalog.Find(sha256)?.DisplayName ?? SaveFileStore.Abbrev(sha256);
        _startSave = new StartSave(sha256, size, name, Catalog.FindNextNetId(sha256) ?? 0);
        _startInfoConnection = null;
        PublishStatus();
        if (_authority is { Phase: NodePhase.SyncingSave, Announced: true } authority)
        {
            SendStartSaveInfo(authority); // the authority was here before the save was chosen
        }

        return true;
    }

    /// <summary>True when <paramref name="node"/> is the authority, the session still has no checkpoint and <paramref name="shaHex"/> is the start save.</summary>
    private bool IsStartSave(SessionNode node, string shaHex) =>
        node.IsAuthority && EffectiveStart() is { } start && string.Equals(start.Sha, shaHex, StringComparison.Ordinal);

    /// <summary>
    /// What a loading authority must load: the chosen start save while the session has no checkpoint, or the current checkpoint's save when an
    /// authority (re)joined a session that already has one (AuthorityLost to AuthorityLoading, M3-21). Null otherwise.
    /// </summary>
    private StartSave? EffectiveStart()
    {
        if (_current is null)
        {
            return _startSave;
        }

        if (_phase == SessionPhase.AuthorityLoading && _current.SaveSha is { } sha && Files.SizeOf(sha, UploadKind.Save) is { } size)
        {
            return new StartSave(sha, size, _current.Name ?? SaveFileStore.Abbrev(sha), _current.NextNetId);
        }

        return null;
    }

    /// <summary>Sends the authority the start save (see <see cref="SetStartSaveAsync"/>) unless it has already loaded it.</summary>
    private void SendStartSaveInfo(SessionNode node)
    {
        if (EffectiveStart() is not { } start || !node.IsAuthority || node.Connection is not { } connection)
        {
            return;
        }

        if (ReferenceEquals(_startInfoConnection, connection))
        {
            return;
        }

        // M3-25: the authority is about to run exactly this save (loaded now, or already running it): the replicated world must match it.
        RollbackWorldToSave(node, start);
        if (LoadedSaveOf(node) is { } loaded && string.Equals(loaded, start.Sha, StringComparison.Ordinal))
        {
            _startInfoConnection = connection;
            return; // the authority's game already runs this save: it reports ready and the session asks it for the first checkpoint
        }

        if (Files.SizeOf(start.Sha, UploadKind.Save) is null)
        {
            LogStartSaveMissing(SaveFileStore.Abbrev(start.Sha));
            _startSave = null;
            PublishStatus();
            return;
        }

        _startInfoConnection = connection;
        var (token, httpUrl, _) = HttpFields(node, Opt, start.Sha, null);
        var info = new SessionSaveInfoT
        {
            CheckpointId = new Id128T(), // not a checkpoint: no id (the authority makes no manifest report)
            Sha256 = [.. Convert.FromHexString(start.Sha)],
            Size = (ulong)start.Size,
            DisplayName = start.Name,
            LocalFileName = SaveFileStore.LocalFileName(start.Sha),
            ManifestSha256 = [],
            ManifestSize = 0,
            HttpUrl = httpUrl,
            ManifestHttpUrl = string.Empty,
            DownloadToken = token,
            GameTime = 0,
        };
        Send(node, ControlFrames.Encode(MsgType.SessionSaveInfo, fbb => SessionSaveInfo.Pack(fbb, info).Value, 512));
        string shown = SaveFileStore.Abbrev(start.Sha);
        LogStartSaveSent(node.PlayerId, shown, start.Size);
    }

    /// <summary>
    /// M3-25: the world mirror must hold exactly what the save the authority loads contains. The authority allocates net ids upwards from the
    /// <c>next_net_id</c> it reported in the save's <c>SaveStarted</c>, so every entity with an id at or above that value was spawned after the save
    /// and is not in it (a plain start save has none: threshold 1 removes everything), and the authority player's own ship (a new self-spawn replaces it,
    /// whatever its id: a later checkpoint also holds the old one in its id range). Those entities are removed through the normal despawn path
    /// (journal, clients' ghosts, avatar bindings), and the authority is told the first id it may hand out (<c>AuthorityAssign.next_net_id</c>) so no
    /// id that was live or is still mapped to a ghost on a client is reused.
    /// </summary>
    private void RollbackWorldToSave(SessionNode node, StartSave start)
    {
        uint threshold = Math.Max(1u, start.NextNetId);
        int removed = _world.RollbackToNetIdFloor(threshold, node.PlayerId);
        string shown = SaveFileStore.Abbrev(start.Sha);
        LogWorldRolledBack(node.PlayerId, shown, threshold, removed);
        if (threshold <= 1)
        {
            return;
        }

        var assign = new AuthorityAssignT
        {
            Grant = true,
            Reason = "world rolled back to the loaded save",
            CheckpointId = _current is { } cp && cp.SaveSha == start.Sha ? cp.Id.ToWire() : new Id128T(),
            NextNetId = threshold,
            StringTableNext = 0,
        };
        Send(node, ControlFrames.Encode(MsgType.AuthorityAssign, fbb => AuthorityAssign.Pack(fbb, assign).Value, 128));
    }

    /// <summary>The authority loaded the start save (it reported <c>SaveReady</c> for it): move it to Loading. It makes no manifest report, nothing is replayed to it.</summary>
    private void OnStartSaveReady(SessionNode node)
    {
        CancelDownload(node.PlayerId);
        Fire(node.PlayerId, NodeTrigger.ReportLoading);
    }

    private static string? LoadedSaveOf(SessionNode node)
    {
        var loaded = node.Attached?.Hello.LoadedSaveSha256;
        return loaded is { Count: 32 } ? Convert.ToHexStringLower([.. loaded]) : null;
    }

    [Microsoft.Extensions.Logging.LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Information, Message = "player {PlayerId}: the authority is sent the session's start save {Sha} ({Bytes} bytes) to load before the first checkpoint")]
    private partial void LogStartSaveSent(int playerId, string sha, long bytes);

    [Microsoft.Extensions.Logging.LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Information, Message = "player {PlayerId}: the authority loads save {Sha}: world rolled back to net ids below {NextNetId} ({Removed} entities removed)")]
    private partial void LogWorldRolledBack(int playerId, string sha, uint nextNetId, int removed);

    [Microsoft.Extensions.Logging.LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "the session's start save {Sha} is no longer in the store: the authority is not sent it")]
    private partial void LogStartSaveMissing(string sha);
}

using X4MP.Core.Net;
using X4MP.Core.Session;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.Core.Saves;

public sealed partial class SaveService
{
    /// <summary>The stored save a session is started from (an admin upload or an older checkpoint): what the authority must load before the first checkpoint exists.</summary>
    private sealed record StartSave(string Sha, long Size, string Name);

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
        _startSave = new StartSave(sha256, size, name);
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
        node.IsAuthority && _current is null && _startSave is { } start && string.Equals(start.Sha, shaHex, StringComparison.Ordinal);

    /// <summary>Sends the authority the start save (see <see cref="SetStartSaveAsync"/>) unless it has already loaded it.</summary>
    private void SendStartSaveInfo(SessionNode node)
    {
        if (_startSave is not { } start || _current is not null || !node.IsAuthority || node.Connection is not { } connection)
        {
            return;
        }

        if (ReferenceEquals(_startInfoConnection, connection))
        {
            return;
        }

        if (LoadedSaveOf(node) is { } loaded && string.Equals(loaded, start.Sha, StringComparison.Ordinal))
        {
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

    [Microsoft.Extensions.Logging.LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "the session's start save {Sha} is no longer in the store: the authority is not sent it")]
    private partial void LogStartSaveMissing(string sha);
}

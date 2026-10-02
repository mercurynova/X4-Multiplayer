using X4MP.Server.Api;

namespace X4MP.Server.Hubs;

/// <summary>
/// What the server calls on an admin browser (server-design 4.6). The method names are the SignalR target names and are mirrored in
/// <see cref="AdminHubEvents"/>; a test keeps the two in step. Team and economy pushes arrive with their REST tasks (M1-T5, M1-E6).
/// </summary>
public interface IAdminClient
{
    /// <summary>1 Hz while the client is in the dashboard topic.</summary>
    Task Dashboard(DashboardSnapshotDto snapshot);

    /// <summary>A node joined, resumed, detached or changed phase (dashboard topic, immediate).</summary>
    Task PlayerChanged(PlayerLiveDto player);

    /// <summary>A player left the session for good (dashboard topic).</summary>
    Task PlayerRemoved(long playerId);

    /// <summary>The session's state or its authority changed (dashboard topic, immediate).</summary>
    Task SessionChanged(SessionSummaryDto session);

    /// <summary>1 Hz while the client is in the galaxy topic.</summary>
    Task GalaxyFrame(GalaxyFrameDto frame);

    /// <summary>4 Hz per subscribed sector.</summary>
    Task SectorFrame(SectorFrameDto frame);

    /// <summary>Log lines of the log tail, batched, filtered for this client.</summary>
    Task LogBatch(List<LogEntryDto> entries);

    /// <summary>1 Hz while the client is in the diagnostics topic.</summary>
    Task Diagnostics(List<ConnectionStatsDto> connections);

    /// <summary>A chat line (chat topic).</summary>
    Task Chat(ChatMessageDto message);

    /// <summary>2 Hz while any transfer is active (dashboard topic); the last push of a transfer has <c>Finished</c> set.</summary>
    Task SaveTransfer(TransferProgressDto transfer);

    /// <summary>An alert was raised or cleared (every connected client).</summary>
    Task Alert(AlertDto alert);

    /// <summary>A setting changed; carries the new values (every connected client).</summary>
    Task SettingsChanged(SettingsDto settings);

    /// <summary>A client's action was refused by the asset permission policy (dashboard topic, at most 5 per second).</summary>
    Task PermissionDenied(PermissionDeniedDto denied);
}

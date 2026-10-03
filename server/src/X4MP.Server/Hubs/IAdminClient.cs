using X4MP.Server.Api;

namespace X4MP.Server.Hubs;

/// <summary>
/// What the server calls on an admin browser (server-design 4.6). The method names are the SignalR target names and are mirrored in
/// <see cref="AdminHubEvents"/>; a test keeps the two in step.
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

    /// <summary>A wallet changed (economy topic). Coalesced per wallet, at most every <c>EconomyWalletIntervalMs</c> (250 ms, 4 Hz).</summary>
    Task WalletChanged(WalletDto wallet);

    /// <summary>A transaction was committed (economy topic, every one; the GUI filters).</summary>
    Task LedgerPosted(LedgerTxDto transaction);

    /// <summary>A loan changed state or balance, including Overdue (economy topic; the latest state of a loan wins when the queue is busy).</summary>
    Task LoanChanged(LoanDto loan);

    /// <summary>A trade changed state or version (economy topic; the latest state of a trade wins when the queue is busy).</summary>
    Task TradeChanged(TradeOfferDto trade);

    /// <summary>An economy event of the log (economy topic, at most 10 per second).</summary>
    Task EconomyEvent(EconomyEventDto economyEvent);

    /// <summary>1 Hz while the economy topic has members: the overview with the state of the auditor.</summary>
    Task EconomySummary(EconomySummaryDto summary);

    /// <summary>An economy alert was raised or cleared: an invariant breach, an overdrawn wallet, an InDoubt trade (economy topic).</summary>
    Task EconomyAlert(AlertDto alert);

    /// <summary>A team was created or changed (name, colour, lock, leader, member count; teams topic).</summary>
    Task TeamUpserted(TeamDto team);

    /// <summary>A team was deleted (teams topic); its members arrive as <see cref="TeamMemberChanged"/>.</summary>
    Task TeamDeleted(long teamId);

    /// <summary>A player was assigned, moved, promoted or unassigned (<c>TeamId</c> null; teams topic). Keyed per player: the latest wins when the queue is busy.</summary>
    Task TeamMemberChanged(TeamMemberDto member);

    /// <summary>The relation matrix changed (teams topic).</summary>
    Task TeamRelationsChanged(TeamRelationsDto relations);

    /// <summary>A team setting changed (teams topic).</summary>
    Task TeamPolicyChanged(TeamPolicyDto policy);

    /// <summary>The picture changed too much to patch (a preset, a player gone): the whole state (teams topic).</summary>
    Task TeamsReset(TeamsStateDto state);

    /// <summary>The session mod policy changed (mods topic): the whole policy with its entries, per-mod player counts filled in only for clients that may see players' mod lists.</summary>
    Task ModPolicyChanged(ModPolicyDto policy);

    /// <summary>A player's extension report arrived (a join, admitted, warned or rejected; mods topic; only for clients that may see players' mod lists). Keyed per player: the latest wins when the queue is busy.</summary>
    Task PlayerModsReported(PlayerModStatusDto status);

    /// <summary>A connection was refused over its mods and its key is bound to no player (an unknown key; mods topic; only for clients that may see players' mod lists). Keyed per key: the latest wins when the queue is busy.</summary>
    Task UnboundRejectionReported(UnboundRejectionDto rejection);

    /// <summary>A node forwarded log lines or a self-test table (dashboard topic). Coalesced per player: at most one push per player every 250 ms, the latest state wins.</summary>
    Task NodeDiagnosticsChanged(NodeDiagnosticsDto diagnostics);

    /// <summary>A connected player is waiting for a team (AdminAssign or an unmade Lobby choice; teams topic).</summary>
    Task PlayerAwaitingTeam(TeamMemberDto member);
}

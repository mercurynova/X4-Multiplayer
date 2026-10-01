using X4MP.Proto;

namespace X4MP.Protocol;

// One line per message in protocol.md section 20. Lanes follow the catalog (Realtime: WorldUpdate,
// EntityStatusBatch, Replication, PlayerState, UdpHello/Ack; Bulk: SaveChunk; everything else Control).
// DamageReport (0x0403) is reserved and deliberately not registered. A test asserts that every
// MsgType value other than Invalid/DamageReport appears here.
//
// verify == null: the table (transitively) contains a FlatBuffers union. Google.FlatBuffers 25.2.10's
// Verifier.VerifyUnion reads the union type byte from the wrong offset, so the generated verifier is
// unusable for these; they rely on the full-read pass (UnPack) alone. See MessageDescriptor.
public sealed partial class MessageRegistry
{
    private void RegisterAll()
    {
        Add<ServerHello>(MsgType.ServerHello, Lane.Control, ServerHello.GetRootAsServerHello, ServerHelloVerify.Verify, t => t.UnPack());
        Add<ClientHello>(MsgType.ClientHello, Lane.Control, ClientHello.GetRootAsClientHello, ClientHelloVerify.Verify, t => t.UnPack());
        Add<Welcome>(MsgType.Welcome, Lane.Control, Welcome.GetRootAsWelcome, WelcomeVerify.Verify, t => t.UnPack());
        Add<Disconnect>(MsgType.Disconnect, Lane.Control, Disconnect.GetRootAsDisconnect, DisconnectVerify.Verify, t => t.UnPack());
        Add<Ping>(MsgType.Ping, Lane.Control, Ping.GetRootAsPing, PingVerify.Verify, t => t.UnPack());
        Add<Pong>(MsgType.Pong, Lane.Control, Pong.GetRootAsPong, PongVerify.Verify, t => t.UnPack());
        Add<UdpHello>(MsgType.UdpHello, Lane.Realtime, UdpHello.GetRootAsUdpHello, UdpHelloVerify.Verify, t => t.UnPack());
        Add<UdpHelloAck>(MsgType.UdpHelloAck, Lane.Realtime, UdpHelloAck.GetRootAsUdpHelloAck, UdpHelloAckVerify.Verify, t => t.UnPack());
        Add<ServerNotice>(MsgType.ServerNotice, Lane.Control, ServerNotice.GetRootAsServerNotice, ServerNoticeVerify.Verify, t => t.UnPack());
        Add<SessionState>(MsgType.SessionState, Lane.Control, SessionState.GetRootAsSessionState, SessionStateVerify.Verify, t => t.UnPack());
        Add<RosterUpdate>(MsgType.RosterUpdate, Lane.Control, RosterUpdate.GetRootAsRosterUpdate, RosterUpdateVerify.Verify, t => t.UnPack());
        Add<SessionSettings>(MsgType.SessionSettings, Lane.Control, SessionSettings.GetRootAsSessionSettings, SessionSettingsVerify.Verify, t => t.UnPack());
        Add<RequestSave>(MsgType.RequestSave, Lane.Control, RequestSave.GetRootAsRequestSave, RequestSaveVerify.Verify, t => t.UnPack());
        Add<SaveStarted>(MsgType.SaveStarted, Lane.Control, SaveStarted.GetRootAsSaveStarted, SaveStartedVerify.Verify, t => t.UnPack());
        Add<SaveUploadBegin>(MsgType.SaveUploadBegin, Lane.Control, SaveUploadBegin.GetRootAsSaveUploadBegin, SaveUploadBeginVerify.Verify, t => t.UnPack());
        Add<SaveUploadAccept>(MsgType.SaveUploadAccept, Lane.Control, SaveUploadAccept.GetRootAsSaveUploadAccept, SaveUploadAcceptVerify.Verify, t => t.UnPack());
        Add<SaveChunk>(MsgType.SaveChunk, Lane.Bulk, SaveChunk.GetRootAsSaveChunk, SaveChunkVerify.Verify, t => t.UnPack());
        Add<SaveChunkAck>(MsgType.SaveChunkAck, Lane.Control, SaveChunkAck.GetRootAsSaveChunkAck, SaveChunkAckVerify.Verify, t => t.UnPack());
        Add<SaveUploadEnd>(MsgType.SaveUploadEnd, Lane.Control, SaveUploadEnd.GetRootAsSaveUploadEnd, SaveUploadEndVerify.Verify, t => t.UnPack());
        Add<SaveStored>(MsgType.SaveStored, Lane.Control, SaveStored.GetRootAsSaveStored, SaveStoredVerify.Verify, t => t.UnPack());
        Add<SessionSaveInfo>(MsgType.SessionSaveInfo, Lane.Control, SessionSaveInfo.GetRootAsSessionSaveInfo, SessionSaveInfoVerify.Verify, t => t.UnPack());
        Add<SaveDownloadRequest>(MsgType.SaveDownloadRequest, Lane.Control, SaveDownloadRequest.GetRootAsSaveDownloadRequest, SaveDownloadRequestVerify.Verify, t => t.UnPack());
        Add<SaveDownloadAccept>(MsgType.SaveDownloadAccept, Lane.Control, SaveDownloadAccept.GetRootAsSaveDownloadAccept, SaveDownloadAcceptVerify.Verify, t => t.UnPack());
        Add<SaveReady>(MsgType.SaveReady, Lane.Control, SaveReady.GetRootAsSaveReady, SaveReadyVerify.Verify, t => t.UnPack());
        Add<LoadStatus>(MsgType.LoadStatus, Lane.Control, LoadStatus.GetRootAsLoadStatus, LoadStatusVerify.Verify, t => t.UnPack());
        Add<NodeReady>(MsgType.NodeReady, Lane.Control, NodeReady.GetRootAsNodeReady, NodeReadyVerify.Verify, t => t.UnPack());
        Add<ManifestReport>(MsgType.ManifestReport, Lane.Control, ManifestReport.GetRootAsManifestReport, ManifestReportVerify.Verify, t => t.UnPack());
        Add<AuthorityAssign>(MsgType.AuthorityAssign, Lane.Control, AuthorityAssign.GetRootAsAuthorityAssign, AuthorityAssignVerify.Verify, t => t.UnPack());
        Add<GalaxyMetadata>(MsgType.GalaxyMetadata, Lane.Control, GalaxyMetadata.GetRootAsGalaxyMetadata, GalaxyMetadataVerify.Verify, t => t.UnPack());
        Add<StringTableAdd>(MsgType.StringTableAdd, Lane.Control, StringTableAdd.GetRootAsStringTableAdd, StringTableAddVerify.Verify, t => t.UnPack());
        Add<GalaxySummary>(MsgType.GalaxySummary, Lane.Control, GalaxySummary.GetRootAsGalaxySummary, GalaxySummaryVerify.Verify, t => t.UnPack());
        Add<EntitySpawn>(MsgType.EntitySpawn, Lane.Control, EntitySpawn.GetRootAsEntitySpawn, EntitySpawnVerify.Verify, t => t.UnPack());
        Add<EntityDespawn>(MsgType.EntityDespawn, Lane.Control, EntityDespawn.GetRootAsEntityDespawn, EntityDespawnVerify.Verify, t => t.UnPack());
        Add<WorldUpdate>(MsgType.WorldUpdate, Lane.Realtime, WorldUpdate.GetRootAsWorldUpdate, WorldUpdateVerify.Verify, t => t.UnPack());
        Add<EntityStatusBatch>(MsgType.EntityStatusBatch, Lane.Realtime, EntityStatusBatch.GetRootAsEntityStatusBatch, EntityStatusBatchVerify.Verify, t => t.UnPack());
        Add<EntityChange>(MsgType.EntityChange, Lane.Control, EntityChange.GetRootAsEntityChange, EntityChangeVerify.Verify, t => t.UnPack());
        Add<EntityCargo>(MsgType.EntityCargo, Lane.Control, EntityCargo.GetRootAsEntityCargo, EntityCargoVerify.Verify, t => t.UnPack());
        Add<CaptureSet>(MsgType.CaptureSet, Lane.Control, CaptureSet.GetRootAsCaptureSet, CaptureSetVerify.Verify, t => t.UnPack());
        Add<SectorComplete>(MsgType.SectorComplete, Lane.Control, SectorComplete.GetRootAsSectorComplete, SectorCompleteVerify.Verify, t => t.UnPack());
        Add<Replication>(MsgType.Replication, Lane.Realtime, Replication.GetRootAsReplication, ReplicationVerify.Verify, t => t.UnPack());
        Add<InterestUpdate>(MsgType.InterestUpdate, Lane.Control, InterestUpdate.GetRootAsInterestUpdate, InterestUpdateVerify.Verify, t => t.UnPack());
        Add<InterestChecksum>(MsgType.InterestChecksum, Lane.Control, InterestChecksum.GetRootAsInterestChecksum, InterestChecksumVerify.Verify, t => t.UnPack());
        Add<ResyncRequest>(MsgType.ResyncRequest, Lane.Control, ResyncRequest.GetRootAsResyncRequest, ResyncRequestVerify.Verify, t => t.UnPack());
        Add<WorldCatchUp>(MsgType.WorldCatchUp, Lane.Control, WorldCatchUp.GetRootAsWorldCatchUp, null, t => t.UnPack());
        Add<InterestHint>(MsgType.InterestHint, Lane.Control, InterestHint.GetRootAsInterestHint, InterestHintVerify.Verify, t => t.UnPack());
        Add<PlayerState>(MsgType.PlayerState, Lane.Realtime, PlayerState.GetRootAsPlayerState, PlayerStateVerify.Verify, t => t.UnPack());
        Add<PlayerShip>(MsgType.PlayerShip, Lane.Control, PlayerShip.GetRootAsPlayerShip, PlayerShipVerify.Verify, t => t.UnPack());
        Add<Intent>(MsgType.Intent, Lane.Control, Intent.GetRootAsIntent, null, t => t.UnPack());
        Add<IntentResult>(MsgType.IntentResult, Lane.Control, IntentResult.GetRootAsIntentResult, IntentResultVerify.Verify, t => t.UnPack());
        Add<GameEvent>(MsgType.GameEvent, Lane.Control, GameEvent.GetRootAsGameEvent, null, t => t.UnPack());
        Add<ChatSend>(MsgType.ChatSend, Lane.Control, ChatSend.GetRootAsChatSend, ChatSendVerify.Verify, t => t.UnPack());
        Add<ChatMessage>(MsgType.ChatMessage, Lane.Control, ChatMessage.GetRootAsChatMessage, ChatMessageVerify.Verify, t => t.UnPack());
        Add<AdminCommand>(MsgType.AdminCommand, Lane.Control, AdminCommand.GetRootAsAdminCommand, null, t => t.UnPack());
        Add<AdminResult>(MsgType.AdminResult, Lane.Control, AdminResult.GetRootAsAdminResult, AdminResultVerify.Verify, t => t.UnPack());
        Add<NodeStats>(MsgType.NodeStats, Lane.Control, NodeStats.GetRootAsNodeStats, NodeStatsVerify.Verify, t => t.UnPack());
        Add<LogForward>(MsgType.LogForward, Lane.Control, LogForward.GetRootAsLogForward, LogForwardVerify.Verify, t => t.UnPack());
        Add<TeamTable>(MsgType.TeamTable, Lane.Control, TeamTable.GetRootAsTeamTable, TeamTableVerify.Verify, t => t.UnPack());
        Add<TeamRelations>(MsgType.TeamRelations, Lane.Control, TeamRelations.GetRootAsTeamRelations, TeamRelationsVerify.Verify, t => t.UnPack());
        Add<TeamChoice>(MsgType.TeamChoice, Lane.Control, TeamChoice.GetRootAsTeamChoice, TeamChoiceVerify.Verify, t => t.UnPack());
        Add<TeamCreateRequest>(MsgType.TeamCreateRequest, Lane.Control, TeamCreateRequest.GetRootAsTeamCreateRequest, TeamCreateRequestVerify.Verify, t => t.UnPack());
        Add<TeamChangeRequest>(MsgType.TeamChangeRequest, Lane.Control, TeamChangeRequest.GetRootAsTeamChangeRequest, TeamChangeRequestVerify.Verify, t => t.UnPack());
        Add<RelationChangeRequest>(MsgType.RelationChangeRequest, Lane.Control, RelationChangeRequest.GetRootAsRelationChangeRequest, RelationChangeRequestVerify.Verify, t => t.UnPack());
        Add<TeamRequestResult>(MsgType.TeamRequestResult, Lane.Control, TeamRequestResult.GetRootAsTeamRequestResult, TeamRequestResultVerify.Verify, t => t.UnPack());
        Add<TeamMemberChanged>(MsgType.TeamMemberChanged, Lane.Control, TeamMemberChanged.GetRootAsTeamMemberChanged, TeamMemberChangedVerify.Verify, t => t.UnPack());
        Add<RelationProposal>(MsgType.RelationProposal, Lane.Control, RelationProposal.GetRootAsRelationProposal, RelationProposalVerify.Verify, t => t.UnPack());
        Add<ReassignPlayerAssets>(MsgType.ReassignPlayerAssets, Lane.Control, ReassignPlayerAssets.GetRootAsReassignPlayerAssets, ReassignPlayerAssetsVerify.Verify, t => t.UnPack());
        Add<WalletUpdate>(MsgType.WalletUpdate, Lane.Control, WalletUpdate.GetRootAsWalletUpdate, WalletUpdateVerify.Verify, t => t.UnPack());
        Add<CreditDelta>(MsgType.CreditDelta, Lane.Control, CreditDelta.GetRootAsCreditDelta, CreditDeltaVerify.Verify, t => t.UnPack());
        Add<EconomyResult>(MsgType.EconomyResult, Lane.Control, EconomyResult.GetRootAsEconomyResult, EconomyResultVerify.Verify, t => t.UnPack());
        Add<CreditTransferRequest>(MsgType.CreditTransferRequest, Lane.Control, CreditTransferRequest.GetRootAsCreditTransferRequest, CreditTransferRequestVerify.Verify, t => t.UnPack());
        Add<PoolDepositRequest>(MsgType.PoolDepositRequest, Lane.Control, PoolDepositRequest.GetRootAsPoolDepositRequest, PoolDepositRequestVerify.Verify, t => t.UnPack());
        Add<PoolWithdrawRequest>(MsgType.PoolWithdrawRequest, Lane.Control, PoolWithdrawRequest.GetRootAsPoolWithdrawRequest, PoolWithdrawRequestVerify.Verify, t => t.UnPack());
        Add<DonateRequest>(MsgType.DonateRequest, Lane.Control, DonateRequest.GetRootAsDonateRequest, DonateRequestVerify.Verify, t => t.UnPack());
        Add<LoanOffer>(MsgType.LoanOffer, Lane.Control, LoanOffer.GetRootAsLoanOffer, LoanOfferVerify.Verify, t => t.UnPack());
        Add<LoanRespond>(MsgType.LoanRespond, Lane.Control, LoanRespond.GetRootAsLoanRespond, LoanRespondVerify.Verify, t => t.UnPack());
        Add<LoanRepay>(MsgType.LoanRepay, Lane.Control, LoanRepay.GetRootAsLoanRepay, LoanRepayVerify.Verify, t => t.UnPack());
        Add<LoanForgive>(MsgType.LoanForgive, Lane.Control, LoanForgive.GetRootAsLoanForgive, LoanForgiveVerify.Verify, t => t.UnPack());
        Add<LoanCancel>(MsgType.LoanCancel, Lane.Control, LoanCancel.GetRootAsLoanCancel, LoanCancelVerify.Verify, t => t.UnPack());
        Add<LoanStatus>(MsgType.LoanStatus, Lane.Control, LoanStatus.GetRootAsLoanStatus, LoanStatusVerify.Verify, t => t.UnPack());
        Add<TradeProposal>(MsgType.TradeProposal, Lane.Control, TradeProposal.GetRootAsTradeProposal, TradeProposalVerify.Verify, t => t.UnPack());
        Add<TradeCounter>(MsgType.TradeCounter, Lane.Control, TradeCounter.GetRootAsTradeCounter, TradeCounterVerify.Verify, t => t.UnPack());
        Add<TradeAccept>(MsgType.TradeAccept, Lane.Control, TradeAccept.GetRootAsTradeAccept, TradeAcceptVerify.Verify, t => t.UnPack());
        Add<TradeCancel>(MsgType.TradeCancel, Lane.Control, TradeCancel.GetRootAsTradeCancel, TradeCancelVerify.Verify, t => t.UnPack());
        Add<TradeStatus>(MsgType.TradeStatus, Lane.Control, TradeStatus.GetRootAsTradeStatus, TradeStatusVerify.Verify, t => t.UnPack());
        Add<TradeResult>(MsgType.TradeResult, Lane.Control, TradeResult.GetRootAsTradeResult, TradeResultVerify.Verify, t => t.UnPack());
        Add<AssetTransferOrder>(MsgType.AssetTransferOrder, Lane.Control, AssetTransferOrder.GetRootAsAssetTransferOrder, AssetTransferOrderVerify.Verify, t => t.UnPack());
        Add<AssetTransferConfirm>(MsgType.AssetTransferConfirm, Lane.Control, AssetTransferConfirm.GetRootAsAssetTransferConfirm, AssetTransferConfirmVerify.Verify, t => t.UnPack());
        Add<TradeQuery>(MsgType.TradeQuery, Lane.Control, TradeQuery.GetRootAsTradeQuery, TradeQueryVerify.Verify, t => t.UnPack());
    }
}

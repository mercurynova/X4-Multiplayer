using System.Reflection;
using System.Text.Json;
using Google.FlatBuffers;
using X4MP.Proto;

namespace X4MP.Protocol.Tests;

/// <summary>One deterministic sample payload for a message type (and, for unions, one body variant).</summary>
public sealed record SampleMessage(MsgType Type, string Variant, object Source)
{
    /// <summary>File-name stem used by the golden vectors, e.g. <c>0x0005_Ping</c> or <c>0x0400_Intent_KillClaim</c>.</summary>
    public string Stem => $"0x{(ushort)Type:X4}_{Type}" + (Variant.Length == 0 ? "" : "_" + Variant);

    /// <summary>The FlatBuffers payload (root table, no frame header).</summary>
    public byte[] Encode()
    {
        var fbb = new FlatBufferBuilder(256);
        var tableType = typeof(MsgType).Assembly.GetType($"X4MP.Proto.{Type}")!;
        var pack = tableType.GetMethod("Pack", BindingFlags.Public | BindingFlags.Static, [typeof(FlatBufferBuilder), Source.GetType()])!;
        var offset = pack.Invoke(null, [fbb, Source])!;
        int value = (int)offset.GetType().GetField("Value")!.GetValue(offset)!;
        fbb.Finish(value);
        return fbb.DataBuffer.ToSizedArray();
    }

    /// <summary>Canonical JSON of the object-API form of a decoded table, for equality comparison.</summary>
    public static string ToJson(IFlatbufferObject decoded)
    {
        object t = decoded.GetType().GetMethod("UnPack")!.Invoke(decoded, null)!;
        return JsonSerializer.Serialize(t, t.GetType());
    }

    public string SourceJson => JsonSerializer.Serialize(Source, Source.GetType());
}

/// <summary>
/// Hand-written, fully populated sample for every MsgType in the catalog (all fields non-default, so a
/// dropped field shows up in the round trip). Used by the round-trip tests and the golden vectors.
/// </summary>
public static class SampleMessages
{
    private static Id128T Id(ulong n) => new() { Lo = n, Hi = n * 3 + 1 };
    private static Vec3fT V(float x, float y, float z) => new() { X = x, Y = y, Z = z };
    private static Rot3fT R(float y, float p, float r) => new() { Yaw = y, Pitch = p, Roll = r };

    private static EntityStateT State(uint netId) => new()
    {
        NetId = netId, Sector = 12, Flags = (ushort)(StateFlags.Boost | StateFlags.Keyframe),
        Px = 64000, Py = -128, Pz = 1_000_000, Yaw = 16384, Pitch = -8192, Roll = 100,
        Vx = 40, Vy = -40, Vz = 7,
    };

    private static EntityRecordT Record(uint netId) => new()
    {
        NetId = netId, Kind = EntityKind.ShipM, Origin = EntityOrigin.AuthorityRuntime, MacroRef = 5, OwnerRef = 6,
        OwnerTeam = 2, OwnerPlayer = 3, ParentNetId = 99, ControllerPlayer = 4, Name = "Ship " + netId,
        Idcode = "ABC-123", Hull = 200, Shield = 100, State = State(netId),
    };

    private static EntityChangeT Change() => new()
    {
        JournalSeq = 77, NetId = 1001, Fields = ChangeField.Owner | ChangeField.Name, OwnerRef = 8, OwnerTeam = 2,
        OwnerPlayer = 3, Name = "Renamed", ParentNetId = 5, MacroRef = 9, Kind = EntityKind.Station,
        ControllerPlayer = 1, CauseTradeId = Id(5),
    };

    private static List<WalletBalanceT> Balances() =>
    [
        new() { Wallet = new WalletRefT { Kind = WalletKind.Player, OwnerId = 3 }, Balance = 1_000_000, Version = 11 },
        new() { Wallet = new WalletRefT { Kind = WalletKind.TeamPool, OwnerId = 1 }, Balance = -5, Version = 12 },
    ];

    private static TeamInfoT Team(ushort id) => new()
    {
        TeamId = id, Name = "Team " + id, ColorRgb = 0xFF8800, FactionSlot = (byte)id, LeaderPlayer = 4, Locked = true,
        MaxMembers = 8, PasswordProtected = true,
        Members = [new TeamMemberT { PlayerId = 4, Name = "Alice", Role = TeamRole.Leader, Online = true }, new TeamMemberT { PlayerId = 5, Name = "Bob", Role = TeamRole.Member, Online = false }],
    };

    private static TeamTableT TeamTable() => new() { Version = 3, Full = true, Teams = [Team(1), Team(2)], Removed = [7, 8] };

    private static TeamRelationsT Relations() => new()
    {
        Version = 4, Full = true, DefaultRelation = TeamRelation.Neutral,
        Entries = [new TeamRelationEntryT { TeamA = 1, TeamB = 2, Relation = TeamRelation.Hostile }, new TeamRelationEntryT { TeamA = 1, TeamB = 3, Relation = TeamRelation.Allied }],
    };

    private static SessionSettingsT Settings() => new()
    {
        Version = 5,
        Team = new TeamPolicyT
        {
            JoinMode = TeamJoinMode.Lobby, AutoAssign = AutoAssignStrategy.Balance, AllowCreateInLobby = true, AllowSelfTeamChange = true,
            MaxTeams = 8, AssetPolicy = TeamAssetPolicy.OwnerAndLeader, AllowFriendlyFire = true, AllowAssetTransfer = true,
            MoveAssetsWithPlayer = MoveAssetsScope.AllOwned, RelationChangePolicy = RelationChangePolicy.LeadersMutualAlly,
        },
        Economy = new EconomySettingsT
        {
            CreditMode = CreditMode.PerPlayer, EffectiveMode = EffectiveCreditMode.PerPlayer, TeamPoolEnabled = false,
            PoolWithdrawPolicy = PoolWithdrawPolicy.LeaderOnly, PoolWithdrawDailyLimit = 500_000, MaxTransferAmount = 1_000_000_000,
            AllowAlliedTransfers = true, DonateScope = EconomyScope.Anyone, LoanScope = EconomyScope.Allied, TradeScope = EconomyScope.Teammates,
            TradeShipsEnabled = false, TradeStationsEnabled = true, MaxOpenTradesPerPlayer = 3, MaxOpenLoansPerPlayer = 2,
        },
    };

    private static TradeItemT Item(TradeItemKind k) => new() { Kind = k, Amount = 1234567890123, WareRef = 8, Asset = 42 };

    private static SampleMessage S(object source, string variant = "")
    {
        var name = source.GetType().Name;
        var type = Enum.Parse<MsgType>(name[..^1]);
        return new SampleMessage(type, variant, source);
    }

    private static SampleMessage Intent(IntentBodyUnion body) =>
        S(new IntentT { RequestKey = Id(1), RequestId = 17, PlayerId = 3, GameTime = 123456.789, Body = body }, body.Type.ToString());

    private static SampleMessage Event(GameEventBodyUnion body) =>
        S(new GameEventT { EventSeq = 900, ServerTimeUs = 5_000_000, GameTime = 4321.5, Sector = 12, Body = body }, body.Type.ToString());

    private static SampleMessage Admin(AdminBodyUnion body) =>
        S(new AdminCommandT { RequestKey = Id(2), RequestId = 18, Body = body }, body.Type.ToString());

    /// <summary>Two valid codec entries (a moving ship and a status-only entry) for the Replication sample.</summary>
    public static byte[] ReplicationEntries() => ReplicationCodec.Encode(
    [
        new ReplicationEntry
        {
            NetId = 1001, Mask = ReplicationMask.Pos | ReplicationMask.Vel | ReplicationMask.Time,
            PosX = 64, PosY = -64, PosZ = 1_000_000, VelX = 4, VelY = -4, VelZ = short.MaxValue, TimeMs = -20,
        },
        new ReplicationEntry { NetId = 1002, Mask = ReplicationMask.Status, Hull = 255, Shield = 7 },
    ]);

    public static IReadOnlyList<SampleMessage> All { get; } = Build();

    private static List<SampleMessage> Build() =>
    [
        // ---- control ----
        S(new ServerHelloT
        {
            ProtocolMajor = 0, ProtocolMinor = 1, ServerVersion = "0.1.0", ServerName = "Test server", SessionId = Id(9),
            Nonce = Enumerable.Range(0, 32).Select(i => (byte)i).ToList(), Auth = AuthMethod.SessionPassword,
            ServerCaps = (ulong)(Capability.UdpRealtime | Capability.Economy), Phase = SessionPhase.Running,
            RequiredGameBuild = "900-611726", SupportedGameBuilds = ["900-611726", "900-611727"], RequiredModVersion = "0.1.0",
            ExtensionsHash = [1, 2, 3, 4],
        }),
        S(new ClientHelloT
        {
            ProtocolMajor = 0, ProtocolMinor = 1, ModVersion = "0.1.0", ModBuild = "abc1234", GameVersion = "9.00", GameBuild = "900-611726",
            X4nativeVersion = "9.0.0", Platform = "win64", ExtensionsHash = [9, 8, 7], Extensions = ["x4mp@0.1.0", "other@1.0"],
            PlayerKey = Enumerable.Range(100, 32).Select(i => (byte)i).ToList(), PlayerName = "Alice",
            RequestedRoles = Role.Client | Role.Admin, ClientCaps = 0x3FFF, AuthProof = [5, 5, 5], AdminProof = [6, 6],
            ResumeToken = Id(10), LastJournalSeq = 4242, LoadedSaveSha256 = [1, 1, 1, 1], CachedSaves = [new SaveRefT { Sha256 = [2, 2, 2] }],
            PreferredTeam = 2,
        }),
        S(new WelcomeT
        {
            PlayerId = 4, GrantedRoles = Role.Client, NegotiatedCaps = 0x1F, ResumeToken = Id(11), Resumed = true, ConnId = 0xDEADBEEF,
            UdpPort = 47781, UdpToken = 0x0123456789ABCDEF, ServerTimeUs = 999_999_999, HeartbeatIntervalMs = 1000, HeartbeatTimeoutMs = 10000,
            ResumeGraceS = 60, HttpBaseUrl = "http://127.0.0.1:47790", MaxGhosts = 4000, TeamId = 2, TeamRole = TeamRole.Leader, FactionSlot = 2,
            Teams = TeamTable(), Relations = Relations(), Settings = Settings(),
        }),
        S(new DisconnectT { Code = DisconnectCode.ClientReload, Message = "reload", Expected = "900-611726", RetryAfterMs = 5000 }),
        S(new PingT { Seq = 1, SendTimeUs = 1_000_000 }),
        S(new PongT { Seq = 1, EchoSendTimeUs = 1_000_000, RecvTimeUs = 1_000_500, ReplyTimeUs = 1_000_600 }),
        S(new UdpHelloT { ConnId = 0xDEADBEEF, UdpToken = 0x0123456789ABCDEF }),
        S(new UdpHelloAckT { ConnId = 0xDEADBEEF }),
        S(new ServerNoticeT { Severity = NoticeSeverity.Warning, Text = "Server restarting soon", DisplayMs = 8000 }),

        // ---- session and saves ----
        S(new SessionStateT
        {
            Phase = SessionPhase.Paused, SessionName = "Co-op", AuthorityPlayer = 1, Paused = true, PauseReason = "admin", GameTime = 3600.25,
            TimeScale = 2.5f, CurrentSaveSha256 = [0xAA, 0xBB], MaxPlayers = 8,
        }),
        S(new RosterUpdateT
        {
            Full = true, Removed = [9],
            Players =
            [
                new PlayerInfoT { PlayerId = 1, Name = "Alice", Roles = Role.Authority | Role.Client, Phase = NodePhase.InGame, TeamId = 1, TeamRole = TeamRole.Leader, ShipNetId = 1001, Sector = 12, PingMs = 23 },
                new PlayerInfoT { PlayerId = 2, Name = "Bob", Roles = Role.Client, Phase = NodePhase.Loading, TeamId = 2, TeamRole = TeamRole.Member, ShipNetId = 0, Sector = 0, PingMs = 80 },
            ],
        }),
        S(Settings()),
        S(new RequestSaveT { RequestId = 5, Reason = SaveReason.JoinRequested, SlotName = "x4mp_checkpoint" }),
        S(new SaveStartedT { RequestId = 5, CheckpointId = Id(12), GameTime = 7200.5, NextNetId = 5000 }),
        S(new SaveUploadBeginT { CheckpointId = Id(12), Kind = UploadKind.Manifest, Size = 123_456_789_012, Sha256 = Enumerable.Repeat((byte)0xCD, 32).ToList(), Name = "checkpoint", GhostsCleaned = true }),
        S(new SaveUploadAcceptT { UploadId = 7, ChunkSize = 262144, ResumeOffset = 524288, WindowChunks = 8 }),
        S(new SaveChunkT { TransferId = 7, Offset = 262144, Data = Enumerable.Range(0, 300).Select(i => (byte)(i * 7)).ToList() }),
        S(new SaveChunkAckT { TransferId = 7, NextOffset = 1_048_576 }),
        S(new SaveUploadEndT { UploadId = 7 }),
        S(new SaveStoredT { UploadId = 7, CheckpointId = Id(12), Kind = UploadKind.Save, Result = SaveStoreResult.StoredNotCurrent, Detail = "ghosts" }),
        S(new SessionSaveInfoT
        {
            CheckpointId = Id(12), Sha256 = [1, 2, 3], Size = 99_999, DisplayName = "Save 12", LocalFileName = "x4mp_010203040506.xml.gz",
            ManifestSha256 = [4, 5, 6], ManifestSize = 300_000, HttpUrl = "http://h/saves/x", ManifestHttpUrl = "http://h/saves/m",
            DownloadToken = "tok", GameTime = 7200.5,
        }),
        S(new SaveDownloadRequestT { Sha256 = [7, 7], Kind = UploadKind.Manifest, Offset = 4096 }),
        S(new SaveDownloadAcceptT { DownloadId = 3, Size = 10_000_000, ChunkSize = 262144, WindowChunks = 8 }),
        S(new SaveReadyT { Sha256 = [1, 2], ManifestSha256 = [3, 4] }),
        S(new LoadStatusT { Phase = NodePhase.Loading, Progress = 0.5f, BytesDone = 5_000_000, Detail = "Loading", Error = DisconnectCode.LoadFailed }),
        S(new NodeReadyT { UniverseEpoch = 0xABCDEF0123456789, LoadedSaveSha256 = [9, 9] }),
        S(new ManifestReportT
        {
            CheckpointId = Id(12), Total = 3000, Matched = 2990, MatchedTiebreakOwner = 4, MatchedTiebreakIdcode = 3, Unmatched = 10, Ambiguous = 2,
            UniverseIdEqual = 2900, UnmatchedSample = [1, 2, 3], DurationMs = 850,
        }),
        S(new AuthorityAssignT { Grant = true, Reason = "migration", CheckpointId = Id(12), NextNetId = 5000, StringTableNext = 321 }),
        S(new GalaxyMetadataT
        {
            SaveSha256 = [1, 2, 3],
            Sectors = [new SectorInfoT { Index = 1, Macro = "cluster_01_sector001_macro", ClusterMacro = "cluster_01_macro", Name = "Argon Prime", OwnerRef = 3, GalaxyPos = V(1.5f, -2.5f, 0) }],
            Links = [new SectorLinkT { From = 1, To = 2, Kind = LinkKind.Gate, FromPos = V(1000, 0, 2000), ToPos = V(-1000, 0, -2000) }],
        }),
        S(new StringTableAddT { Entries = [new StringEntryT { Index = 1, Kind = StringKind.Macro, Value = "cluster_01_sector001_macro" }, new StringEntryT { Index = 2, Kind = StringKind.Faction, Value = "argon" }] }),
        S(new GalaxySummaryT
        {
            GameTime = 3600.25,
            Sectors = [new SectorSummaryT { Sector = 1, ShipsXs = 1, ShipsS = 2, ShipsM = 3, ShipsL = 4, ShipsXl = 5, Stations = 6 }],
        }),
        S(new ServerSettingsUpdateT
        {
            Version = 5,
            Entries = [new SettingEntryT { Key = "Mods.ModListVisibility", Value = "AdminsAndViewers" }, new SettingEntryT { Key = "Replication.TickRateHz", Value = "30" }],
        }),

        // ---- world ----
        S(new EntitySpawnT { JournalSeq = 77, Entities = [Record(1001), Record(1002)] }),
        S(new EntityDespawnT { JournalSeq = 78, Entries = [new DespawnEntryT { NetId = 1001, KillerNetId = 1002, Reason = DespawnReason.Destroyed }] }),
        S(new WorldUpdateT { AuthorityTick = 123456, CaptureTimeUs = 9_876_543_210, GameTime = 3600.25, States = [State(1001), State(1002)] }),
        S(new EntityStatusBatchT { CaptureTimeUs = 9_876_543_210, Statuses = [new EntityStatusT { NetId = 1001, Hull = 128, Shield = 64, StatusFlags = 3 }] }),
        S(Change()),
        S(new EntityCargoT { JournalSeq = 79, NetId = 1001, Wares = [new WareAmountT { WareRef = 8, Amount = 100 }, new WareAmountT { WareRef = 9, Amount = -1 }] }),
        S(new CaptureSetT
        {
            Epoch = 6, Sectors = [new CaptureSectorT { Sector = 12, RateHz = 5 }],
            Focus = [new CaptureFocusT { Sector = 12, Center = V(100, 200, 300), RadiusM = 15000, RateHz = 20 }],
        }),
        S(new SectorCompleteT { Sector = 12, Epoch = 6, EntityCount = 321 }),
        S(new ReplicationT { ServerTick = 777, ServerTimeUs = 5_000_000, AuthorityGameTime = 3600.25, EntryCount = 2, Entries = [.. ReplicationEntries()] }),
        S(new InterestUpdateT { Epoch = 6, Full = true, Sectors = [new SectorTierT { Sector = 12, Tier = InterestTier.Near }, new SectorTierT { Sector = 13, Tier = InterestTier.Adjacent }] }),
        S(new InterestChecksumT { ServerTick = 777, Count = 300, XorHash = 0xFEDCBA9876543210 }),
        S(new ResyncRequestT { Sectors = [12, 13], Reason = "checksum" }),
        S(new WorldCatchUpT
        {
            CheckpointId = Id(12), Final = true,
            Entries =
            [
                new JournalEntryT { Seq = 1, Body = WorldMutationUnion.FromEntityRecord(Record(2001)) },
                new JournalEntryT { Seq = 2, Body = WorldMutationUnion.FromJournalDespawn(new JournalDespawnT { Entry = new DespawnEntryT { NetId = 2001, KillerNetId = 0, Reason = DespawnReason.Removed } }) },
                new JournalEntryT { Seq = 3, Body = WorldMutationUnion.FromEntityChange(Change()) },
                new JournalEntryT { Seq = 4, Body = WorldMutationUnion.FromEntityCargo(new EntityCargoT { JournalSeq = 4, NetId = 2001, Wares = [new WareAmountT { WareRef = 1, Amount = 2 }] }) },
            ],
        }),
        S(new InterestHintT { TargetSector = 14, EtaMs = 4000, Reason = HintReason.GateApproach }),

        // ---- player ----
        S(new PlayerStateT
        {
            Seq = 100, SampleTimeUs = 5_000_000, NetId = 1001, Sector = 12, Flags = (ushort)StateFlags.TravelDrive, Px = 6400, Py = -6400, Pz = 12800,
            Yaw = 1000, Pitch = -1000, Roll = 500, Hull = 250, Shield = 10, TargetNetId = 1002,
        }),
        S(new PlayerShipT
        {
            RequestKey = Id(13), ShipMacro = "ship_arg_s_fighter_01_a_macro", Name = "Pilot", Idcode = "XYZ-789", Sector = 12, Px = 1, Py = 2, Pz = 3,
            Yaw = 4, Pitch = 5, Roll = 6, Hull = 255, Shield = 255, LocalComponentId = 0x1122334455667788, PlayerId = 3,
        }),
        S(new OnFootStateT
        {
            PlayerId = 3, Seq = 77, SampleTimeUs = 5_100_000, Mode = OnFootMode.Walking, ContainerNetId = 1500, OuterContainerNetId = 0,
            Room = new RoomKeyT { MacroRef = 9, AnchorX = -120, AnchorY = 0, AnchorZ = 340, PathHash = 0xBEEF, Kind = RoomKind.Lounge, Roomtype = 4 },
            Px = 2048, Py = 0, Pz = -1024, Cx = 640, Cy = 0, Cz = -320, Yaw = 1200, LookPitch = 128, Anim = OnFootAnim.Walk, EmoteId = 0, Flags = 1,
        }),

        // ---- intents (one sample per union body) ----
        Intent(IntentBodyUnion.FromKillClaim(new KillClaimT { Target = 1002, Killer = 1001, WeaponMacroRef = 4 })),
        Intent(IntentBodyUnion.FromHitReport(new HitReportT { Hits = [new HitEntryT { Target = 1002, HullDamage = 12.5f, ShieldDamage = 3.25f }] })),
        Intent(IntentBodyUnion.FromPlayerDeath(new PlayerDeathT { Killer = 1002 })),
        Intent(IntentBodyUnion.FromStationBuildRequest(new StationBuildRequestT { Macro = "station_gen_factory_macro", ConstructionPlan = "plan_1", Sector = 12, Position = V(1, 2, 3), Rotation = R(0.5f, 0.25f, 0.125f), LocalComponentId = 555 })),
        Intent(IntentBodyUnion.FromTradeReport(new TradeReportT { Station = 1500, Ship = 1001, WareRef = 8, Amount = -50, UnitPrice = 120 })),
        Intent(IntentBodyUnion.FromCaptureReport(new CaptureReportT { Target = 1003, NewOwnerRef = 6 })),
        Intent(IntentBodyUnion.FromAssetOrder(new AssetOrderT { Asset = 1004, Order = OrderKind.Patrol, Target = 1005, Sector = 12, Position = V(5, 6, 7), Immediate = true, ParamsJson = "{\"a\":1}" })),
        Intent(IntentBodyUnion.FromAssetRename(new AssetRenameT { Asset = 1004, Name = "New name" })),
        Intent(IntentBodyUnion.FromAssetGift(new AssetGiftT { Asset = 1004, ToTeam = 2, ToPlayer = 5 })),
        S(new IntentResultT { RequestKey = Id(1), RequestId = 17, PlayerId = 3, Status = IntentStatus.Rejected, Reason = RejectReason.NotAllied, Detail = "no", ResultNetId = 1500 }),

        // ---- game events (one sample per union body) ----
        Event(GameEventBodyUnion.FromKillEvent(new KillEventT { Victim = 1002, VictimMacroRef = 4, VictimOwnerRef = 5, VictimKind = EntityKind.ShipL, VictimTeam = 2, Killer = 1001, KillerPlayer = 3, KillerTeam = 1 })),
        Event(GameEventBodyUnion.FromPlayerDiedEvent(new PlayerDiedEventT { PlayerId = 3, Killer = 1002, KillerPlayer = 0 })),
        Event(GameEventBodyUnion.FromPlayerSpawnedEvent(new PlayerSpawnedEventT { PlayerId = 3, ShipNetId = 1001 })),
        Event(GameEventBodyUnion.FromStationBuiltEvent(new StationBuiltEventT { Station = 1500, MacroRef = 4, OwnerRef = 6, BuilderPlayer = 3 })),
        Event(GameEventBodyUnion.FromTradeEvent(new TradeEventT { Station = 1500, Ship = 1001, PlayerId = 3, WareRef = 8, Amount = 10, UnitPrice = 99 })),
        Event(GameEventBodyUnion.FromCaptureEvent(new CaptureEventT { Target = 1003, OldOwnerRef = 5, OldTeam = 0, NewTeam = 2, PlayerId = 3 })),
        Event(GameEventBodyUnion.FromSectorOwnerEvent(new SectorOwnerEventT { OldOwnerRef = 5, NewOwnerRef = 6 })),
        Event(GameEventBodyUnion.FromPlayerConnectionEvent(new PlayerConnectionEventT { PlayerId = 3, Joined = true, Code = DisconnectCode.Kicked })),
        Event(GameEventBodyUnion.FromCustomEvent(new CustomEventT { Name = "custom", Json = "{\"k\":\"v\"}" })),
        Event(GameEventBodyUnion.FromTeamEvent(new TeamEventT { PlayerId = 3, FromTeam = 1, ToTeam = 2, TeamA = 1, TeamB = 2, Relation = TeamRelation.Allied })),
        Event(GameEventBodyUnion.FromEconomyEvent(new EconomyEventT { Kind = 2, FromPlayer = 3, ToPlayer = 4, Amount = 5000, RefId = Id(14) })),

        // ---- chat, admin ----
        S(new ChatSendT { Channel = ChatChannel.Whisper, ToPlayer = 4, Text = "hello" }),
        S(new ChatMessageT { FromPlayer = 3, FromName = "Alice", Channel = ChatChannel.Team, Text = "hi team", ServerTimeUs = 8_000_000 }),
        Admin(AdminBodyUnion.FromKickCmd(new KickCmdT { PlayerId = 3, Reason = "afk" })),
        Admin(AdminBodyUnion.FromBanCmd(new BanCmdT { PlayerId = 3, Reason = "grief", DurationS = 3600 })),
        Admin(AdminBodyUnion.FromBroadcastCmd(new BroadcastCmdT { Text = "notice", Severity = 2 })),
        Admin(AdminBodyUnion.FromSetPausedCmd(new SetPausedCmdT { Paused = true })),
        Admin(AdminBodyUnion.FromForceCheckpointCmd(new ForceCheckpointCmdT())),
        Admin(AdminBodyUnion.FromMigrateAuthorityCmd(new MigrateAuthorityCmdT { TargetPlayer = 2 })),
        Admin(AdminBodyUnion.FromSetTimeScaleCmd(new SetTimeScaleCmdT { Scale = 4.5f })),
        Admin(AdminBodyUnion.FromSetSettingCmd(new SetSettingCmdT { Key = "k", Value = "v" })),
        Admin(AdminBodyUnion.FromAssignTeamCmd(new AssignTeamCmdT { PlayerId = 3, TeamId = 2, Role = TeamRole.Leader, AssetScope = MoveAssetsScope.AllOwned })),
        Admin(AdminBodyUnion.FromCreateTeamCmd(new CreateTeamCmdT { Name = "Red", ColorRgb = 0xFF0000, FactionSlot = 3, Locked = true, MaxMembers = 4 })),
        Admin(AdminBodyUnion.FromUpdateTeamCmd(new UpdateTeamCmdT { TeamId = 2, Name = "Blue", ColorRgb = 0x0000FF, LeaderPlayer = 4, Locked = true, MaxMembers = 6 })),
        Admin(AdminBodyUnion.FromDeleteTeamCmd(new DeleteTeamCmdT { TeamId = 2, MoveMembersTo = 1 })),
        Admin(AdminBodyUnion.FromSetRelationCmd(new SetRelationCmdT { TeamA = 1, TeamB = 2, Relation = TeamRelation.Hostile })),
        Admin(AdminBodyUnion.FromApplyTeamPresetCmd(new ApplyTeamPresetCmdT { Preset = "versus", Confirm = true })),
        Admin(AdminBodyUnion.FromSetCreditModeCmd(new SetCreditModeCmdT { Mode = CreditMode.Shared, Confirm = true })),
        Admin(AdminBodyUnion.FromAdjustWalletCmd(new AdjustWalletCmdT { Wallet = new WalletRefT { Kind = WalletKind.TeamShared, OwnerId = 1 }, Amount = -250, Reason = "fix" })),
        Admin(AdminBodyUnion.FromFreezeEconomyCmd(new FreezeEconomyCmdT { PlayerId = 3, Frozen = true })),
        Admin(AdminBodyUnion.FromCancelTradeCmd(new CancelTradeCmdT { TradeId = Id(15), Reason = "stuck" })),
        Admin(AdminBodyUnion.FromSetLoanStateCmd(new SetLoanStateCmdT { LoanId = Id(16), State = LoanState.Forgiven, Reason = "admin" })),
        S(new AdminResultT { RequestId = 18, Ok = true, Message = "done" }),

        // ---- telemetry ----
        S(new NodeStatsT
        {
            Fps = 59.5f, FrameMsP95 = 21.25f, GameTime = 3600.25, Ghosts = 1500, SuppressedLocal = 800, PendingMainThreadJobs = 3, TcpSendQueueBytes = 4096,
            UdpRxLossPct = 0.5f, UdpActive = true, RxBytesPerS = 10_000, TxBytesPerS = 5_000, InterpDelayMs = 150, ClockOffsetUs = -1234, RttMs = 31.5f,
            MemoryMb = 4096, MdHookState = FeatureState.Ok, TeamSetupState = FeatureState.Starting,
        }),
        S(new LogForwardT { Lines = [new LogLineT { Level = LogLevel.Warn, TimeUs = 12_345_678, Text = "something" }] }),

        // ---- teams ----
        S(TeamTable()),
        S(Relations()),
        S(new TeamChoiceT { RequestKey = Id(20), TeamId = 2, Password = [1, 2, 3] }),
        S(new TeamCreateRequestT { RequestKey = Id(21), Name = "Green", ColorRgb = 0x00FF00 }),
        S(new TeamChangeRequestT { RequestKey = Id(22), TeamId = 3, Password = [4, 5] }),
        S(new RelationChangeRequestT { RequestKey = Id(23), OtherTeam = 3, Relation = TeamRelation.Allied }),
        S(new TeamRequestResultT { RequestKey = Id(23), Status = TeamRequestStatus.Pending, Reason = TeamRejectReason.NotLeader, Detail = "wait", TeamId = 3 }),
        S(new TeamMemberChangedT { PlayerId = 3, FromTeam = 1, ToTeam = 2, Role = TeamRole.Leader, ByAdmin = true, TableVersion = 9 }),
        S(new RelationProposalT { FromTeam = 1, ToTeam = 2, Relation = TeamRelation.Allied, ExpiresInS = 120 }),
        S(new ReassignPlayerAssetsT { PlayerId = 3, FromTeam = 1, ToTeam = 2, Scope = MoveAssetsScope.ShipOnly }),

        // ---- economy ----
        S(new WalletUpdateT { Balances = Balances(), Reason = LedgerReason.Transfer, RefId = Id(30), EffectiveMode = EffectiveCreditMode.Shared, AckedDeltaSeq = 55 }),
        S(new CreditDeltaT { RequestKey = Id(31), PlayerId = 3, TeamId = 1, Amount = -12345, Source = CreditSource.StationIncome, RefEventSeq = 9, GameTime = 3600.25, Seq = 56 }),
        S(new EconomyResultT { RequestKey = Id(32), Status = EconomyStatus.Rejected, Reason = EconomyReject.RequestIdReuse, Detail = "poor", RefId = Id(33), Balances = Balances() }),
        S(new CreditTransferRequestT { RequestKey = Id(34), ToPlayer = 4, Amount = 1000, Memo = "thanks" }),
        S(new PoolDepositRequestT { RequestKey = Id(35), Amount = 2000 }),
        S(new PoolWithdrawRequestT { RequestKey = Id(36), Amount = 3000 }),
        S(new DonateRequestT { RequestKey = Id(37), ToPlayer = 4, Amount = 4000, Memo = "gift" }),
        S(new LoanOfferT { RequestKey = Id(38), Borrower = 4, Principal = 1_000_000, RepayTotal = 1_100_000, DueInS = 86400, OfferTtlS = 300, AutoRepayPct = 25, Memo = "loan" }),
        S(new LoanRespondT { RequestKey = Id(39), LoanId = Id(40), Accept = true }),
        S(new LoanRepayT { RequestKey = Id(41), LoanId = Id(40), Amount = 500 }),
        S(new LoanForgiveT { RequestKey = Id(42), LoanId = Id(40), Amount = 0 }),
        S(new LoanCancelT { RequestKey = Id(43), LoanId = Id(40) }),
        S(new LoanStatusT { LoanId = Id(40), Lender = 3, Borrower = 4, State = LoanState.Withdrawn, Principal = 1_000_000, RepayTotal = 1_100_000, Repaid = 100, Forgiven = 50, CreatedTimeUs = 1_000, DueTimeUs = 2_000_000, Memo = "m" }),
        S(new TradeProposalT { RequestKey = Id(44), Counterparty = 4, Give = [Item(TradeItemKind.Credits)], Want = [Item(TradeItemKind.Ship), Item(TradeItemKind.Ware)], TtlS = 300, Memo = "deal" }),
        S(new TradeCounterT { RequestKey = Id(45), TradeId = Id(46), BaseVersion = 2, Give = [Item(TradeItemKind.Station)], Want = [Item(TradeItemKind.Credits)] }),
        S(new TradeAcceptT { RequestKey = Id(47), TradeId = Id(46), Version = 3, ReceiveIntoAsset = 1500 }),
        S(new TradeCancelT { RequestKey = Id(48), TradeId = Id(46) }),
        S(new TradeStatusT
        {
            TradeId = Id(46), Version = 3, State = TradeState.InDoubt,
            Initiator = new TradeSideT { PlayerId = 3, TeamId = 1, Give = [Item(TradeItemKind.Credits)], AcceptedVersion = 3 },
            Counterparty = new TradeSideT { PlayerId = 4, TeamId = 2, Give = [Item(TradeItemKind.Ship)], AcceptedVersion = 2 },
            ExpiresTimeUs = 9_000_000, Memo = "m",
        }),
        S(new TradeResultT { TradeId = Id(46), Version = 3, State = TradeState.RolledBack, Reason = EconomyReject.OutOfRange, Detail = "not in the same sector" }),
        S(new AssetTransferOrderT
        {
            TradeId = Id(46), DeadlineMs = 15000,
            Lines =
            [
                new AssetTransferLineT { Kind = AssetTransferKind.OwnerChange, Asset = 1004, ToTeam = 2, ToPlayer = 4, WareRef = 0, Amount = 0, DestAsset = 0 },
                new AssetTransferLineT { Kind = AssetTransferKind.WareMove, Asset = 1500, ToTeam = 0, ToPlayer = 0, WareRef = 8, Amount = 250, DestAsset = 1600 },
            ],
        }),
        S(new AssetTransferConfirmT { TradeId = Id(46), Ok = false, FailedLine = 1, Compensated = true, Error = "no cargo" }),
        S(new TradeQueryT { TradeId = Id(46) }),
    ];
}

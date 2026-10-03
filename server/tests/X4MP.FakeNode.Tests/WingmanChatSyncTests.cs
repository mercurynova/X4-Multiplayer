using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.FakeNode.Tests;

/// <summary>M3-05: what a fake client knows about player ships (own avatar, poses, arrival statistics), the wingman controller and the chat echo.</summary>
public sealed class WingmanChatSyncTests
{
    private sealed class Clock
    {
        public double Now { get; set; }
    }

    private const ReplicationMask Full = ReplicationMask.Sector | ReplicationMask.Pos | ReplicationMask.Rot | ReplicationMask.Vel | ReplicationMask.Flags | ReplicationMask.Status;

    private static FakeClientSession Session(Clock clock, int ownPlayer = 2, bool echo = false) =>
        new(new FakeWorld(FakeGalaxy.Generate(42, new GalaxyOptions { SectorCount = 10, ShipCount = 100 })), verify: false, () => clock.Now) { OwnPlayerId = ownPlayer, ChatEcho = echo };

    private static Frame AsFrame(MsgType type, byte[] payload) =>
        new(type, FrameOptions.None, MessageRegistry.Default.GetDescriptor(type).Lane, payload);

    private static EntityStateT State(uint netId, ushort sector, double x, double y, double z, double yaw = 0) => new()
    {
        NetId = netId, Sector = sector, Px = Quantize.Position(x), Py = Quantize.Position(y), Pz = Quantize.Position(z), Yaw = Quantize.Rotation(yaw),
    };

    private static Frame SpawnShip(uint netId, string name, ushort controller, ushort sector = 3, double x = 0, double z = 0, EntityOrigin origin = EntityOrigin.PlayerShip) =>
        AsFrame(MsgType.EntitySpawn, MessageEncoder.EncodePayload(b => EntitySpawn.Pack(b, new EntitySpawnT
        {
            Entities =
            [
                new EntityRecordT
                {
                    NetId = netId, Kind = EntityKind.ShipS, Origin = origin, Name = name, ControllerPlayer = controller, OwnerPlayer = controller, OwnerTeam = 1,
                    State = State(netId, sector, x, 0, z),
                },
            ],
        }), 512));

    private static Frame Entry(uint netId, ushort sector, double x, double y, double z, double vx = 0, double vz = 0, double yaw = 0, uint tick = 1)
    {
        var entry = new ReplicationEntry
        {
            NetId = netId, Mask = Full, Sector = sector, PosX = Quantize.Position(x), PosY = Quantize.Position(y), PosZ = Quantize.Position(z),
            Yaw = Quantize.Rotation(yaw), VelX = (short)(vx * 4), VelZ = (short)(vz * 4), Hull = 255, Shield = 255,
        };
        return AsFrame(MsgType.Replication, MessageEncoder.EncodePayload(b => Replication.Pack(b, new ReplicationT
        {
            ServerTick = tick, ServerTimeUs = 0, AuthorityGameTime = 0, EntryCount = 1, Entries = [.. ReplicationCodec.Encode([entry])],
        }), 128));
    }

    private static Frame Chat(ushort from, ChatChannel channel, string text) =>
        AsFrame(MsgType.ChatMessage, MessageEncoder.EncodePayload(b => ChatMessage.Pack(b, new ChatMessageT { FromPlayer = from, FromName = "P" + from, Channel = channel, Text = text }), 256));

    // ------------------------------------------------------------------ session: avatar, poses, statistics

    [Fact]
    public void TheClientRecognisesItsOwnAvatarFromTheSpawn()
    {
        var clock = new Clock();
        var s = Session(clock, ownPlayer: 2);
        Assert.Null(s.OwnAvatar);
        Assert.False(s.OwnAvatarReady.IsCompleted);

        s.Handle(SpawnShip(5001, "[MP] Host", controller: 1, x: 100));
        Assert.Null(s.OwnAvatar);

        s.Handle(SpawnShip(5002, "[MP] Me", controller: 2, sector: 4, x: 640, z: -320));
        Assert.True(s.OwnAvatarReady.IsCompleted);
        var avatar = s.OwnAvatar!.Value;
        Assert.Equal(5002u, avatar.NetId);
        Assert.Equal((ushort)4, avatar.Sector);
        Assert.Equal(640, avatar.Position.X, 1);
        Assert.Equal(-320, avatar.Position.Z, 1);
    }

    [Fact]
    public void PoseOfAPlayerShipIsFoundByNameAndFollowsTheEntries()
    {
        var clock = new Clock();
        var s = Session(clock);
        s.Handle(SpawnShip(5001, "[MP] Alice", controller: 3, sector: 3, x: 10, z: 20));
        Assert.Null(s.FindPlayerPose("Nobody"));

        var spawnPose = s.FindPlayerPose("Alice")!;
        Assert.Equal(5001u, spawnPose.NetId);
        Assert.Equal(10, spawnPose.Pos.X, 1);

        clock.Now = 1;
        s.Handle(Entry(5001, 3, 500, 5, -250, vx: 100, vz: -50, yaw: 0.5));
        clock.Now = 1.25;
        var pose = s.FindPlayerPose("alice")!; // case-insensitive
        Assert.Equal(500, pose.Pos.X, 1);
        Assert.Equal(-250, pose.Pos.Z, 1);
        Assert.Equal(100, pose.Vel.X, 1);
        Assert.Equal(-50, pose.Vel.Z, 1);
        Assert.Equal(0.5, pose.Yaw, 3);
        Assert.Equal(0.25, pose.AgeSeconds, 3);
        Assert.Equal((ushort)3, pose.Sector);

        // a gate jump arrives as a new sector with the entry
        s.Handle(Entry(5001, 6, -18000, 0, 0, tick: 2));
        Assert.Equal((ushort)6, s.FindPlayerPose("[MP] Alice")!.Sector);
    }

    [Fact]
    public void AParkedAvatarKeepsItsNameAndLosesItsController()
    {
        var clock = new Clock();
        var s = Session(clock);
        s.Handle(SpawnShip(5001, "[MP] Alice", controller: 3));
        Assert.Equal(3, s.ControllerOf(5001));

        s.Handle(AsFrame(MsgType.EntityChange, MessageEncoder.EncodePayload(
            b => EntityChange.Pack(b, new EntityChangeT { NetId = 5001, Fields = ChangeField.Controller, ControllerPlayer = 0 }), 64)));

        Assert.Equal(0, s.ControllerOf(5001));
        Assert.Equal(1, s.ControllerChanges);
        Assert.NotNull(s.FindPlayerPose("Alice (offline)")); // the mod calls it "[MP] Alice (offline)"; the pose is still there
        Assert.Null(s.ControllerOf(9999));
    }

    [Fact]
    public void NpcShipsAreNotTrackedAsPlayerShips()
    {
        var clock = new Clock();
        var s = Session(clock);
        s.Handle(SpawnShip(77, "trader 77", controller: 0, origin: EntityOrigin.AuthorityRuntime));
        s.Handle(Entry(77, 3, 1, 2, 3));
        Assert.Empty(s.PlayerShipIds);
        Assert.Empty(s.SyncSummaries());
        Assert.Null(s.FindPlayerPose("trader 77"));
    }

    [Fact]
    public void ArrivalStatisticsShowTheRateAndTheGaps()
    {
        var clock = new Clock();
        var s = Session(clock);
        s.SetOwnPose(3, new Vec3(0, 0, 0));
        s.Handle(SpawnShip(5001, "[MP] Alice", controller: 3, sector: 3));

        // 10 s at 20 Hz in the same sector, 400 m away
        uint tick = 0;
        for (int i = 0; i < 200; i++)
        {
            clock.Now = 1 + (i * 0.05);
            s.Handle(Entry(5001, 3, 400, 0, 0, vx: 250, tick: ++tick));
        }

        // a pause of 1.2 s (a gate jump), then 5 s more
        for (int i = 0; i < 100; i++)
        {
            clock.Now = 11 + 1.2 + (i * 0.05);
            s.Handle(Entry(5001, 3, 400, 0, 0, vx: 250, tick: ++tick));
        }

        var sum = Assert.Single(s.SyncSummaries());
        Assert.Equal(5001u, sum.NetId);
        Assert.Equal("[MP] Alice", sum.Name);
        Assert.Equal(300, sum.Entries);
        Assert.Equal(300, sum.NearEntries);
        Assert.InRange(sum.NearRateHz, 19.5, 20.5); // the pause is not a flowing gap, so it does not drag the rate down
        Assert.InRange(sum.RateHz, 17, 19.5); // the overall rate does see it
        Assert.InRange(sum.GapP50Ms, 49, 51);
        Assert.InRange(sum.GapP95Ms, 49, 51);
        Assert.InRange(sum.GapMaxMs, 1200, 1300);
        Assert.InRange(sum.SpeedMaxMps, 249, 251);
        Assert.Contains("[sync] net=5001 player=[MP] Alice", sum.ToLine("Bot02"));
        Assert.Contains("near_rate_hz=20.0", sum.ToLine("Bot02"));
    }

    [Fact]
    public void EntriesFromAnotherSectorOrFarAwayAreNotNear()
    {
        var clock = new Clock();
        var s = Session(clock);
        s.SetOwnPose(3, new Vec3(0, 0, 0));
        s.Handle(SpawnShip(5001, "[MP] Alice", controller: 3, sector: 3));
        s.Handle(SpawnShip(5002, "[MP] Bob", controller: 4, sector: 5));
        for (int i = 0; i < 40; i++)
        {
            clock.Now = i * 0.05;
            s.Handle(Entry(5001, 3, 20000, 0, 0, tick: (uint)i + 1)); // same sector, 20 km: out of the 15 km Near sphere
            s.Handle(Entry(5002, 5, 100, 0, 0, tick: (uint)i + 1)); // close in space but another sector
        }

        Assert.All(s.SyncSummaries(), x => Assert.Equal(0, x.NearEntries));
        Assert.All(s.SyncSummaries(), x => Assert.Equal(40, x.Entries));
    }

    [Fact]
    public void TheOwnShipIsDroppedFromTheGhostsWhenTheServerLeavesItOutOfItsChecksum()
    {
        var clock = new Clock();
        var s = Session(clock, ownPlayer: 2);
        s.Handle(SpawnShip(5001, "[MP] Host", controller: 1));
        s.Handle(SpawnShip(5002, "[MP] Me", controller: 2));
        Assert.Equal(2, s.Ghosts); // before M3-01 the server counts the own avatar as a ghost
        Assert.False(s.OwnShipExcludedByServer);

        // the server's checksum covers only the host ship
        var resync = s.Handle(AsFrame(MsgType.InterestChecksum, MessageEncoder.EncodePayload(
            b => InterestChecksum.CreateInterestChecksum(b, 1, 1, InterestHash.Mix(5001)), 48)));

        Assert.Empty(resync);
        Assert.True(s.OwnShipExcludedByServer);
        Assert.Equal(1, s.Ghosts);
        Assert.Equal(0, s.ResyncsRequested);

        // a later spawn answer for the own avatar (a rejoin) does not bring it back as a ghost, but is still known as the own avatar
        s.Handle(SpawnShip(5002, "[MP] Me", controller: 2, x: 77));
        Assert.Equal(1, s.Ghosts);
        Assert.Equal(77, s.OwnAvatar!.Value.Position.X, 1);
    }

    [Fact]
    public void TheOwnShipStaysAGhostWhileTheServerStillCountsIt()
    {
        var clock = new Clock();
        var s = Session(clock, ownPlayer: 2);
        s.Handle(SpawnShip(5001, "[MP] Host", controller: 1));
        s.Handle(SpawnShip(5002, "[MP] Me", controller: 2));
        var resync = s.Handle(AsFrame(MsgType.InterestChecksum, MessageEncoder.EncodePayload(
            b => InterestChecksum.CreateInterestChecksum(b, 1, 2, InterestHash.Mix(5001) ^ InterestHash.Mix(5002)), 48)));
        Assert.Empty(resync);
        Assert.False(s.OwnShipExcludedByServer);
        Assert.Equal(2, s.Ghosts);
        Assert.Equal(1, s.ChecksumsOk);
    }

    [Fact]
    public void TheOwnAvatarGhostNeverGoesStaleBecauseTheServerSendsNoEntriesForIt()
    {
        var clock = new Clock();
        var s = Session(clock, ownPlayer: 2);
        s.Handle(SpawnShip(5001, "[MP] Host", controller: 1));
        s.Handle(SpawnShip(5002, "[MP] Me", controller: 2));
        clock.Now = 100;
        s.Handle(Entry(5001, 3, 1, 2, 3)); // the host's ship keeps getting entries, the own avatar never does
        s.CheckStale();
        Assert.Equal(0, s.Errors);

        s.Handle(SpawnShip(5003, "[MP] Alice", controller: 3));
        clock.Now = 200;
        s.CheckStale();
        Assert.Equal(2, s.Errors); // the host ship and Alice went silent for 100 s; the own avatar is exempt
        Assert.DoesNotContain(s.Violations, v => v.NetId == 5002);
    }

    // ------------------------------------------------------------------ chat echo

    [Fact]
    public void ChatEchoAnswersOnTheSameChannelAndNeverEchoesAnEcho()
    {
        var clock = new Clock();
        var s = Session(clock, ownPlayer: 2, echo: true);

        var all = Assert.Single(s.Handle(Chat(5, ChatChannel.All, "hello")));
        var sent = MessageRegistry.Default.Decode<ChatSend>(AsFrame(all.Type, all.Payload)).UnPack();
        Assert.Equal(MsgType.ChatSend, all.Type);
        Assert.Equal(ChatChannel.All, sent.Channel);
        Assert.Equal("echo: hello", sent.Text);

        var team = MessageRegistry.Default.Decode<ChatSend>(AsFrame(MsgType.ChatSend, Assert.Single(s.Handle(Chat(5, ChatChannel.Team, "/t go"))).Payload)).UnPack();
        Assert.Equal(ChatChannel.Team, team.Channel);

        var whisper = MessageRegistry.Default.Decode<ChatSend>(AsFrame(MsgType.ChatSend, Assert.Single(s.Handle(Chat(7, ChatChannel.Whisper, "psst"))).Payload)).UnPack();
        Assert.Equal(ChatChannel.Whisper, whisper.Channel);
        Assert.Equal((ushort)7, whisper.ToPlayer); // a whisper is answered to the sender only

        Assert.Empty(s.Handle(Chat(5, ChatChannel.All, "echo: hello"))); // an echo is never echoed (two echo bots would answer each other for ever)
        Assert.Empty(s.Handle(Chat(2, ChatChannel.All, "my own words"))); // not its own messages
        Assert.Empty(s.Handle(Chat(0, ChatChannel.System, "server notice"))); // not the server's
        Assert.Empty(s.Handle(Chat(5, ChatChannel.Admin, "admin only")));

        Assert.Equal(7, s.ChatReceived);
        Assert.Equal(3, s.ChatEchoed);
        Assert.Equal("hello", s.ChatLog[0].Text);
        Assert.Equal("P5", s.ChatLog[0].FromName);
    }

    [Fact]
    public void WithoutTheEchoOptionChatIsOnlyRecorded()
    {
        var s = Session(new Clock(), echo: false);
        Assert.Empty(s.Handle(Chat(5, ChatChannel.All, "hello")));
        Assert.Equal(1, s.ChatReceived);
        Assert.Equal(0, s.ChatEchoed);
    }

    [Fact]
    public void AnEchoIsCutToTheServersLimit()
    {
        var s = Session(new Clock(), echo: true);
        var reply = Assert.Single(s.Handle(Chat(5, ChatChannel.All, new string('x', 256))));
        Assert.True(MessageRegistry.Default.Decode<ChatSend>(AsFrame(reply.Type, reply.Payload)).UnPack().Text.Length <= 256);
    }

    // ------------------------------------------------------------------ wingman

    private static FakeClientSession.PlayerPose Pose(ushort sector, Vec3 pos, Vec3 vel, double yaw = 0, double age = 0) =>
        new(9001, "[MP] Leader", 1, sector, pos, vel, yaw, 0, age);

    [Fact]
    public void AWingmanKeepsStationAroundAMovingTarget()
    {
        FakeClientSession.PlayerPose? target = null;
        var w = new FakeWingman(() => target, 3, new Vec3(0, 0, 0), slot: 1, speedMps: 350, radiusMetres: 400);
        var leader = new Vec3(0, 0, 0);
        double maxStep = 0;
        var last = w.Position;
        double worstError = 0;
        for (long tick = 0; tick < 20 * 60; tick++)
        {
            leader += new Vec3(250, 0, 0) * 0.05;
            target = Pose(3, leader, new Vec3(250, 0, 0));
            var state = w.NextSample(tick);
            maxStep = Math.Max(maxStep, (w.Position - last).Length);
            last = w.Position;
            Assert.Equal((ushort)3, state.Sector);
            if (tick > 20 * 20)
                worstError = Math.Max(worstError, Math.Abs((w.Position - leader).Length - 400));
        }

        Assert.True(maxStep <= 350 * 0.05 + 1e-6, $"a wingman never exceeds its top speed (step {maxStep:F2} m)");
        Assert.True(worstError < 120, $"after 20 s the wingman circles at about the wished distance (worst error {worstError:F0} m)");
        Assert.True(w.FollowedSamples > 1000);
    }

    [Fact]
    public void AWingmanFollowsAGateJumpWithATeleport()
    {
        FakeClientSession.PlayerPose? target = Pose(3, new Vec3(0, 0, 0), default);
        var w = new FakeWingman(() => target, 3, new Vec3(300, 0, 0), slot: 0, speedMps: 350, radiusMetres: 400);
        var first = w.NextSample(0);
        Assert.NotEqual(0, first.Flags & (ushort)StateFlags.Teleport); // the first sample places the ship at its avatar
        for (long t = 1; t < 40; t++)
            Assert.Equal(0, w.NextSample(t).Flags & (ushort)StateFlags.Teleport);

        target = Pose(8, new Vec3(-17000, 0, 3000), default);
        var jump = w.NextSample(40);
        Assert.Equal((ushort)8, jump.Sector);
        Assert.NotEqual(0, jump.Flags & (ushort)StateFlags.Teleport);
        Assert.InRange((w.Position - new Vec3(-17000, 0, 3000)).Length, 300, 500);
        Assert.Equal(0, w.NextSample(41).Flags & (ushort)StateFlags.Teleport);
        Assert.Equal(1, w.SectorChanges);
    }

    [Fact]
    public void AFormationWingmanSitsBehindItsTargetAndTurnsWithIt()
    {
        var w = new FakeWingman(() => null, 3, default, slot: 0, speedMps: 350, radiusMetres: 400, WingmanMode.Formation);
        var ahead = w.Slot(Pose(3, new Vec3(0, 0, 0), default, yaw: 0)); // the target faces +z
        Assert.True(ahead.Z < 0, "behind the target");
        var turned = w.Slot(Pose(3, new Vec3(0, 0, 0), default, yaw: Math.PI / 2)); // faces +x
        Assert.True(turned.X < 0, "behind the target after it turned");
        Assert.InRange(ahead.Length, 100, 600);
    }

    [Fact]
    public void AWingmanThatDoesNotKnowItsTargetHoldsItsPlaceAndKeepsSending()
    {
        var w = new FakeWingman(() => null, 3, new Vec3(10, 20, 30), slot: 0, speedMps: 350, radiusMetres: 400) { NetId = 5002 };
        PlayerStateT last = null!;
        for (long t = 0; t < 100; t++)
            last = w.NextSample(t);
        Assert.Equal(new Vec3(10, 20, 30), w.Position);
        Assert.Equal(5002u, last.NetId);
        Assert.Equal(0, w.FollowedSamples);
        Assert.Equal(100u, last.Seq);
    }

    [Fact]
    public void ThePlayerWhoIsTheTargetDoesNotFollowItself()
    {
        var o = CliParser.Parse(["swarm", "--clients", "3", "--with-authority", "--wingman", "Bot01"]).Options!;
        Assert.True(o.AvatarsActive);
        Assert.Null(FakeAvatarFlow.WingmanTargetFor(o, "bot01"));
        Assert.Equal("Bot01", FakeAvatarFlow.WingmanTargetFor(o, "Bot02"));
        Assert.Equal(350, o.WingmanSpeed);
    }

    [Fact]
    public void TheNewCliOptionsParseAndAreChecked()
    {
        var o = CliParser.Parse(["client", "--avatars", "--chat-echo", "--wingman", "Alice", "--wingman-speed", "500", "--wingman-radius", "250", "--wingman-mode", "formation",
            "--host-ship-macro", "ship_arg_m_x_macro"]).Options!;
        Assert.True(o.Avatars);
        Assert.True(o.ChatEcho);
        Assert.Equal(500, o.WingmanSpeed);
        Assert.Equal(250, o.WingmanRadius);
        Assert.Equal(WingmanMode.Formation, o.WingmanMode);
        Assert.Equal("ship_arg_m_x_macro", o.HostShipMacro);

        var a = CliParser.Parse(["authority", "--avatar-macro", "ship_par_s_x_macro", "--avatar-offset", "800", "--host-name", "Cap"]).Options!;
        Assert.Equal("ship_par_s_x_macro", a.AvatarMacro);
        Assert.Equal(800, a.AvatarOffset);
        Assert.Equal("Cap", a.HostName);

        Assert.False(CliParser.Parse(["authority", "--wingman", "x"]).Ok);
        Assert.False(CliParser.Parse(["client", "--wingman-speed", "0"]).Ok);
        Assert.False(CliParser.Parse(["client", "--wingman-mode", "line"]).Ok);
    }

    [Fact]
    public void APlayerCanBePlacedAtItsAvatar()
    {
        var galaxy = FakeGalaxy.Generate(42, new GalaxyOptions { SectorCount = 20, ShipCount = 100 });
        var p = new FakePlayer(galaxy, 42, 0, ClientBehavior.Patrol);
        ushort sector = galaxy.PlayableSectors[2];
        p.PlaceAt(sector, new Vec3(123, 4, 5));
        Assert.Equal(sector, p.Sector);
        var first = p.Step(0);
        Assert.Equal(sector, first.Sector);
        Assert.Equal(Quantize.Position(123), first.Px);
        Assert.NotEqual(0, first.Flags & (ushort)StateFlags.Teleport);
        Assert.Equal(sector, p.Route[0]);
    }
}

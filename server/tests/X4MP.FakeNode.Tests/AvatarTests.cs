using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.FakeNode.Tests;

/// <summary>M3-05: the fake authority answers <c>PlayerShip</c> with avatars next to a self-spawned host ship, parks them on leave and keeps them across resumes.</summary>
public sealed class AvatarTests
{
    private static FakeAuthority NewAuthority(FakeAvatarOptions? avatars = null)
    {
        var authority = new FakeAuthority(new FakeWorld(FakeGalaxy.Generate(42, new GalaxyOptions { SectorCount = 20, ShipCount = 200 })), new FakeAuthorityOptions { Avatars = avatars ?? new FakeAvatarOptions { RosterWait = TimeSpan.Zero } });
        authority.Teams.ApplyWelcome(new WelcomeT { PlayerId = 1, TeamId = 1 });
        return authority;
    }

    private static Frame AsFrame(OutMessage m) => new(m.Type, FrameOptions.None, MessageRegistry.Default.GetDescriptor(m.Type).Lane, m.Payload);

    private static RosterUpdateT Roster(params (ushort Id, string Name, ushort Team)[] players) => new()
    {
        Full = false,
        Removed = [],
        Players = [.. players.Select(p => new PlayerInfoT { PlayerId = p.Id, Name = p.Name, TeamId = p.Team, Phase = NodePhase.InGame, Roles = Role.Client })],
    };

    private static PlayerShipT Request(ushort player, ushort sector = 3, int px = 64 * 1500, string macro = "ship_arg_s_fighter_01_a_macro") => new()
    {
        PlayerId = player, ShipMacro = macro, Name = "Host ship", Idcode = "HST-001", Sector = sector, Px = px, Py = 0, Pz = 64 * -1200, Hull = 255, Shield = 255,
    };

    private static IReadOnlyList<EntityRecordT> Spawned(IEnumerable<OutMessage> messages) =>
        [.. messages.Where(m => m.Type == MsgType.EntitySpawn)
            .SelectMany(m => MessageRegistry.Default.Decode<EntitySpawn>(AsFrame(m)).UnPack().Entities)];

    private static IReadOnlyList<StringEntryT> StringAdds(IEnumerable<OutMessage> messages) =>
        [.. messages.Where(m => m.Type == MsgType.StringTableAdd).SelectMany(m => MessageRegistry.Default.Decode<StringTableAdd>(AsFrame(m)).UnPack().Entries)];

    private static double Distance(EntityStateT a, EntityStateT b) =>
        Math.Sqrt(Math.Pow(Quantize.PositionToMetres(a.Px) - Quantize.PositionToMetres(b.Px), 2)
                  + Math.Pow(Quantize.PositionToMetres(a.Py) - Quantize.PositionToMetres(b.Py), 2)
                  + Math.Pow(Quantize.PositionToMetres(a.Pz) - Quantize.PositionToMetres(b.Pz), 2));

    [Fact]
    public void TheFirstRequestSelfSpawnsTheHostShipWhereTheClientStandsAndSpawnsTheAvatarNextToIt()
    {
        var authority = NewAuthority();
        authority.Avatars.NoteRoster(Roster((1, "BotAuthority", 1), (2, "Alice", 1)));
        authority.Avatars.OnPlayerShip(Request(2));

        var messages = authority.Avatars.Drain(1.0);
        var records = Spawned(messages);
        Assert.Equal(2, records.Count);
        var host = records[0];
        var avatar = records[1];

        // the host ship: same macro, sector and position as the request, controlled by the authority's player, shown as [MP] Host
        Assert.Equal("[MP] Host", host.Name);
        Assert.Equal(EntityOrigin.PlayerShip, host.Origin);
        Assert.Equal((ushort)1, host.ControllerPlayer);
        Assert.Equal((ushort)3, host.State.Sector);
        Assert.Equal(64 * 1500, host.State.Px);
        Assert.Equal(64 * -1200, host.State.Pz);

        // the avatar: [MP] <player>, controlled by the requester, a real macro, 300 to 600 m from the host ship, same sector
        Assert.Equal("[MP] Alice", avatar.Name);
        Assert.Equal(EntityOrigin.PlayerShip, avatar.Origin);
        Assert.Equal(EntityKind.ShipS, avatar.Kind);
        Assert.Equal((ushort)2, avatar.ControllerPlayer);
        Assert.Equal((ushort)2, avatar.OwnerPlayer);
        Assert.Equal((ushort)1, avatar.OwnerTeam);
        Assert.Equal(host.State.Sector, avatar.State.Sector);
        double distance = Distance(host.State, avatar.State);
        Assert.InRange(distance, 300, 600);
        Assert.NotEqual(host.NetId, avatar.NetId);
        Assert.All(records, r => Assert.True(r.NetId > authority.World.Galaxy.Entities.Count));
        Assert.Equal(authority.Avatars.Host!.NetId, host.NetId);
        Assert.Equal(1, authority.Avatars.Provisioned);
    }

    [Fact]
    public void RealMacroAndTeamFactionTravelInTheStringTableBeforeTheSpawn()
    {
        var authority = NewAuthority();
        authority.Avatars.NoteRoster(Roster((2, "Alice", 1)));
        authority.Avatars.OnPlayerShip(Request(2));
        var messages = authority.Avatars.Drain(1.0);

        var adds = StringAdds(messages);
        Assert.Contains(adds, e => e.Value == "ship_arg_s_fighter_01_a_macro" && e.Kind == StringKind.Macro);
        Assert.Contains(adds, e => e.Value == "x4mp_team_1" && e.Kind == StringKind.Faction);
        // every string a spawn refers to is known (from the galaxy table or an add sent earlier) when that spawn goes out
        var known = Enumerable.Range(1, (int)adds.Min(e => e.Index) - 1).Select(i => (uint)i).ToHashSet();
        foreach (var m in messages)
        {
            if (m.Type == MsgType.StringTableAdd)
            {
                foreach (var e in MessageRegistry.Default.Decode<StringTableAdd>(AsFrame(m)).UnPack().Entries)
                    known.Add(e.Index);
            }
            else if (m.Type == MsgType.EntitySpawn)
            {
                foreach (var r in MessageRegistry.Default.Decode<EntitySpawn>(AsFrame(m)).UnPack().Entities)
                {
                    Assert.Contains(r.MacroRef, known);
                    Assert.Contains(r.OwnerRef, known);
                }
            }
        }

        var avatar = Spawned(messages)[1];
        Assert.Equal(authority.Strings.Index("ship_arg_s_fighter_01_a_macro"), avatar.MacroRef);
        Assert.Equal(authority.Strings.Index("x4mp_team_1"), avatar.OwnerRef);

        // a second player on the same team: nothing new to add
        authority.Avatars.NoteRoster(Roster((3, "Bob", 1)));
        authority.Avatars.OnPlayerShip(Request(3));
        Assert.Empty(StringAdds(authority.Avatars.Drain(2.0)));
    }

    [Fact]
    public void TheAvatarMacroAndTheOffsetComeFromTheOptions()
    {
        var authority = NewAuthority(new FakeAvatarOptions { StarterShipMacro = "ship_par_m_frigate_01_a_macro", SpawnOffsetMeters = 1000, RosterWait = TimeSpan.Zero, HostName = "Captain" });
        authority.Avatars.NoteRoster(Roster((2, "Alice", 2)));
        authority.Avatars.OnPlayerShip(Request(2, macro: "ship_arg_m_trans_container_01_a_macro"));
        var records = Spawned(authority.Avatars.Drain(1.0));

        Assert.Equal("[MP] Captain", records[0].Name);
        Assert.Equal(EntityKind.ShipM, records[1].Kind);
        Assert.Equal(authority.Strings.Index("ship_par_m_frigate_01_a_macro"), records[1].MacroRef);
        Assert.Equal(authority.Strings.Index("ship_arg_m_trans_container_01_a_macro"), records[0].MacroRef); // the host ship keeps the macro the client stands in
        Assert.Equal(authority.Strings.Index("x4mp_team_2"), records[1].OwnerRef);
        Assert.InRange(Distance(records[0].State, records[1].State), 850, 1150);
    }

    [Fact]
    public void AvatarsGetDifferentSpotsAndTheHostShipIsSpawnedOnce()
    {
        var authority = NewAuthority();
        authority.Avatars.NoteRoster(Roster((2, "Alice", 1), (3, "Bob", 1), (4, "Cy", 1), (5, "Di", 1)));
        for (ushort p = 2; p <= 5; p++)
            authority.Avatars.OnPlayerShip(Request(p));
        var records = Spawned(authority.Avatars.Drain(1.0));

        Assert.Equal(5, records.Count); // host + 4
        var avatars = records.Skip(1).ToList();
        Assert.Equal(4, avatars.Select(a => a.NetId).Distinct().Count());
        Assert.Equal(4, avatars.Select(a => (a.State.Px, a.State.Py, a.State.Pz)).Distinct().Count());
        Assert.All(avatars, a => Assert.InRange(Distance(records[0].State, a.State), 295, 605));
        Assert.Equal(4, authority.Avatars.Snapshot().Count);
    }

    [Fact]
    public void ARepeatedRequestGetsTheSameAvatarBackWithoutADuplicate()
    {
        var authority = NewAuthority();
        authority.Avatars.NoteRoster(Roster((2, "Alice", 1)));
        authority.Avatars.OnPlayerShip(Request(2));
        var first = Spawned(authority.Avatars.Drain(1.0));
        uint netId = first[1].NetId;

        // the server re-forwards held requests after an authority resume; the avatar must survive and nothing is created twice
        authority.Avatars.OnPlayerShip(Request(2));
        var again = Spawned(authority.Avatars.Drain(5.0));

        Assert.Single(again);
        Assert.Equal(netId, again[0].NetId);
        Assert.Equal((ushort)2, again[0].ControllerPlayer);
        Assert.Equal(1, authority.Avatars.Provisioned);
        Assert.Equal(1, authority.Avatars.Reissued);
        Assert.Single(authority.Avatars.Snapshot());
        Assert.Equal(authority.Avatars.Host!.NetId, first[0].NetId);
    }

    [Fact]
    public void ALeavingPlayerLeavesItsAvatarParkedAndARejoinTakesItBackWhereItWas()
    {
        var authority = NewAuthority();
        authority.Avatars.NoteRoster(Roster((2, "Alice", 1)));
        authority.Avatars.OnPlayerShip(Request(2));
        uint netId = Spawned(authority.Avatars.Drain(1.0))[1].NetId;

        // the relayed state of the avatar moves it (the server stamps the avatar's net_id on it)
        authority.Avatars.OnPlayerState(new PlayerStateT { Seq = 5, NetId = netId, Sector = 4, Px = 64 * 9000, Py = 64 * 10, Pz = 64 * -20 });

        authority.Avatars.NoteRoster(new RosterUpdateT { Full = false, Players = [], Removed = [2] });
        var leave = authority.Avatars.Drain(2.0);
        var change = Assert.Single(leave);
        Assert.Equal(MsgType.EntityChange, change.Type);
        var decoded = MessageRegistry.Default.Decode<EntityChange>(AsFrame(change)).UnPack();
        Assert.Equal(netId, decoded.NetId);
        Assert.Equal(ChangeField.Controller, decoded.Fields);
        Assert.Equal((ushort)0, decoded.ControllerPlayer);
        Assert.False(authority.Avatars.AvatarOf(2)!.Online);
        Assert.Equal(1, authority.Avatars.Leaves);

        // a second removal changes nothing
        authority.Avatars.NoteRoster(new RosterUpdateT { Full = false, Players = [], Removed = [2] });
        Assert.Empty(authority.Avatars.Drain(3.0));

        // rejoin: the existing avatar, controlled again, at the pose the authority last saw
        authority.Avatars.NoteRoster(Roster((2, "Alice", 1)));
        authority.Avatars.OnPlayerShip(Request(2));
        var back = Spawned(authority.Avatars.Drain(4.0));
        var record = Assert.Single(back);
        Assert.Equal(netId, record.NetId);
        Assert.Equal((ushort)2, record.ControllerPlayer);
        Assert.Equal((ushort)4, record.State.Sector);
        Assert.Equal(64 * 9000, record.State.Px);
        Assert.Equal(1, authority.Avatars.Provisioned);
    }

    [Fact]
    public void AFullRosterWithoutAnOnlinePlayerParksItsAvatar()
    {
        var authority = NewAuthority();
        authority.Avatars.NoteRoster(Roster((2, "Alice", 1), (3, "Bob", 1)));
        authority.Avatars.OnPlayerShip(Request(2));
        authority.Avatars.OnPlayerShip(Request(3));
        authority.Avatars.Drain(1.0);

        var full = Roster((3, "Bob", 1));
        full.Full = true;
        authority.Avatars.NoteRoster(full);
        var parked = authority.Avatars.Drain(2.0);

        Assert.Single(parked);
        Assert.False(authority.Avatars.AvatarOf(2)!.Online);
        Assert.True(authority.Avatars.AvatarOf(3)!.Online);
    }

    [Fact]
    public void ARequestWaitsAMomentForTheRosterToNameItsPlayer()
    {
        var authority = NewAuthority(new FakeAvatarOptions { RosterWait = TimeSpan.FromMinutes(5) });
        authority.Avatars.OnPlayerShip(Request(2));
        Assert.Empty(authority.Avatars.Drain(1.0)); // no name yet: held back

        authority.Avatars.NoteRoster(Roster((2, "Alice", 1)));
        var records = Spawned(authority.Avatars.Drain(1.1));
        Assert.Equal("[MP] Alice", records[1].Name);

        // with a wait of zero the avatar is made at once with a placeholder name
        var impatient = NewAuthority();
        impatient.Avatars.OnPlayerShip(Request(9));
        Assert.Equal("[MP] Player9", Spawned(impatient.Avatars.Drain(1.0))[1].Name);
    }

    [Theory]
    [InlineData("ship_arg_s_fighter_01_a_macro", EntityKind.ShipS)]
    [InlineData("ship_par_xs_ut_01_a_macro", EntityKind.ShipXS)]
    [InlineData("ship_tel_m_frigate_01_a_macro", EntityKind.ShipM)]
    [InlineData("ship_spl_l_destroyer_01_a_macro", EntityKind.ShipL)]
    [InlineData("ship_ter_xl_carrier_01_a_macro", EntityKind.ShipXL)]
    [InlineData("something_odd", EntityKind.ShipS)]
    public void TheShipClassComesFromTheMacro(string macro, EntityKind kind) => Assert.Equal(kind, FakeAvatars.KindOf(macro));
}

using X4MP.Core.Relay;
using X4MP.Core.Session;
using X4MP.Core.Tests.Session;
using X4MP.Core.Tests.World;
using X4MP.Proto;

namespace X4MP.Core.Tests.Relay;

/// <summary>The on-foot presence relay (ADR-046, on-foot-presence.md 3.2): container fan-out, roster, appearance, interactions.</summary>
public sealed class RelayOnFootTests
{
    private const ulong OnFootCap = (ulong)Capability.OnFootPresence;
    private const uint Station = 1500;
    private const uint OtherStation = 1600;

    private static List<OnFootStateT> States(JoinedNode node) =>
        [.. node.Connection.SentOf(MsgType.OnFootState).Select(f => f.Decode<OnFootState>().UnPack())];

    private static List<PresenceRosterT> Rosters(JoinedNode node) =>
        [.. node.Connection.SentOf(MsgType.PresenceRoster).Select(f => f.Decode<PresenceRoster>().UnPack())];

    private static List<PlayerAppearanceT> Appearances(JoinedNode node) =>
        [.. node.Connection.SentOf(MsgType.PlayerAppearance).Select(f => f.Decode<PlayerAppearance>().UnPack())];

    private static List<PlayerInteractionT> Interactions(JoinedNode node) =>
        [.. node.Connection.SentOf(MsgType.PlayerInteraction).Select(f => f.Decode<PlayerInteraction>().UnPack())];

    private static async Task SpawnStationAsync(RelayRig rig, JoinedNode authority, uint netId)
    {
        await rig.SendPayloadAsync(authority, MsgType.EntitySpawn, WorldKit.SpawnPayload(WorldKit.Rec(netId, EntityKind.Station, 1)));
    }

    private static async Task DropAsync(RelayRig rig, JoinedNode node)
    {
        node.Connection.Drop();
        long until = Environment.TickCount64 + 5000;
        while ((await rig.Actor.GetSnapshotAsync()).Nodes.Any(n => n.PlayerId == node.PlayerId && n.Connected))
        {
            Assert.True(Environment.TickCount64 < until, "the node was never detached");
            await Task.Delay(5);
        }
    }

    // ------------------------------------------------------------------ fan-out by container

    [Fact]
    public async Task StateReachesOnlyThePlayersOnFootInTheSameContainer()
    {
        await using var rig = new RelayRig();
        var authority = await rig.JoinAuthorityAsync();
        await SpawnStationAsync(rig, authority, Station);
        await SpawnStationAsync(rig, authority, OtherStation);
        var alice = await rig.JoinInGameAsync("Alice", caps: OnFootCap);
        var bob = await rig.JoinInGameAsync("Bob", caps: OnFootCap);
        var carol = await rig.JoinInGameAsync("Carol", caps: OnFootCap);

        await rig.SendAsync(bob, MsgType.OnFootState, RelayFrames.OnFoot(1, Station));
        await rig.SendAsync(carol, MsgType.OnFootState, RelayFrames.OnFoot(1, OtherStation));
        await rig.SendAsync(alice, MsgType.OnFootState, RelayFrames.OnFoot(1, Station, claimedPlayerId: 99));

        var received = Assert.Single(States(bob));
        Assert.Equal(alice.PlayerId, received.PlayerId); // the client's value is overwritten
        Assert.Equal(Station, received.ContainerNetId);
        Assert.Empty(States(carol)); // another container
        Assert.Empty(States(authority)); // the authority's human is not on foot
        Assert.Equal(1, rig.Relay.Stats.OnFootStatesRelayed);
    }

    [Fact]
    public async Task MovingToAnotherContainerSendsTheStateToThePeersOfBothContainers()
    {
        await using var rig = new RelayRig();
        var authority = await rig.JoinAuthorityAsync();
        await SpawnStationAsync(rig, authority, Station);
        await SpawnStationAsync(rig, authority, OtherStation);
        var alice = await rig.JoinInGameAsync("Alice", caps: OnFootCap);
        var bob = await rig.JoinInGameAsync("Bob", caps: OnFootCap);
        var carol = await rig.JoinInGameAsync("Carol", caps: OnFootCap);

        await rig.SendAsync(bob, MsgType.OnFootState, RelayFrames.OnFoot(1, Station));
        await rig.SendAsync(carol, MsgType.OnFootState, RelayFrames.OnFoot(1, OtherStation));
        await rig.SendAsync(alice, MsgType.OnFootState, RelayFrames.OnFoot(1, Station));
        await rig.SendAsync(alice, MsgType.OnFootState, RelayFrames.OnFoot(2, OtherStation, flags: 1));

        Assert.Equal(OtherStation, States(bob).Last().ContainerNetId); // the old container learns about the move
        Assert.Equal(OtherStation, States(carol).Last().ContainerNetId);
    }

    [Fact]
    public async Task LeavingFootSendsALeaveStateToTheContainer()
    {
        await using var rig = new RelayRig();
        var authority = await rig.JoinAuthorityAsync();
        await SpawnStationAsync(rig, authority, Station);
        var alice = await rig.JoinInGameAsync("Alice", caps: OnFootCap);
        var bob = await rig.JoinInGameAsync("Bob", caps: OnFootCap);

        await rig.SendAsync(bob, MsgType.OnFootState, RelayFrames.OnFoot(1, Station));
        await rig.SendAsync(alice, MsgType.OnFootState, RelayFrames.OnFoot(1, Station));
        await rig.SendAsync(alice, MsgType.OnFootState, RelayFrames.OnFoot(2, 0, mode: OnFootMode.None));

        Assert.Equal(OnFootMode.None, States(bob).Last().Mode);
    }

    [Fact]
    public async Task APlayerWhoDisconnectsSendsALeaveState()
    {
        await using var rig = new RelayRig();
        var authority = await rig.JoinAuthorityAsync();
        await SpawnStationAsync(rig, authority, Station);
        var alice = await rig.JoinInGameAsync("Alice", caps: OnFootCap);
        var bob = await rig.JoinInGameAsync("Bob", caps: OnFootCap);

        await rig.SendAsync(bob, MsgType.OnFootState, RelayFrames.OnFoot(1, Station));
        await rig.SendAsync(alice, MsgType.OnFootState, RelayFrames.OnFoot(1, Station));
        await DropAsync(rig, alice);

        Assert.Equal(OnFootMode.None, States(bob).Last().Mode);
    }

    [Fact]
    public async Task NodesWithoutTheCapabilityReceiveNothing()
    {
        await using var rig = new RelayRig();
        var authority = await rig.JoinAuthorityAsync();
        await SpawnStationAsync(rig, authority, Station);
        var alice = await rig.JoinInGameAsync("Alice", caps: OnFootCap);
        var bob = await rig.JoinInGameAsync("Bob");

        await rig.SendAsync(bob, MsgType.OnFootState, RelayFrames.OnFoot(1, Station));
        await rig.SendAsync(alice, MsgType.OnFootState, RelayFrames.OnFoot(1, Station));

        Assert.Empty(States(bob));
        Assert.Empty(Rosters(bob));
        Assert.Empty(Appearances(bob));
    }

    // ------------------------------------------------------------------ validation

    [Fact]
    public async Task AnUnknownContainerIsDropped()
    {
        await using var rig = new RelayRig();
        var authority = await rig.JoinAuthorityAsync();
        await SpawnStationAsync(rig, authority, Station);
        var alice = await rig.JoinInGameAsync("Alice", caps: OnFootCap);
        var bob = await rig.JoinInGameAsync("Bob", caps: OnFootCap);

        await rig.SendAsync(bob, MsgType.OnFootState, RelayFrames.OnFoot(1, Station));
        await rig.SendAsync(alice, MsgType.OnFootState, RelayFrames.OnFoot(1, 4242));

        Assert.Empty(States(bob));
        Assert.Equal(1, rig.Relay.Stats.OnFootStatesDropped);
        Assert.False(rig.Relay.TryGetOnFoot(alice.PlayerId, out _));
    }

    [Fact]
    public async Task AMatchedContainerIdNeedsNoMirrorEntry()
    {
        // M3b: station interiors have no net id before M4, so both nodes derive a "matched" container id from the game state
        // (top bit set). The server must accept it without a mirror entry, or nobody on a station would ever be relayed.
        await using var rig = new RelayRig();
        var authority = await rig.JoinAuthorityAsync();
        await SpawnStationAsync(rig, authority, Station);
        var alice = await rig.JoinInGameAsync("Alice", caps: OnFootCap);
        var bob = await rig.JoinInGameAsync("Bob", caps: OnFootCap);

        const uint matched = RelayModule.MatchedContainerBit | 0x1234_5678u;
        await rig.SendAsync(bob, MsgType.OnFootState, RelayFrames.OnFoot(1, matched));
        await rig.SendAsync(alice, MsgType.OnFootState, RelayFrames.OnFoot(1, matched));

        Assert.NotEmpty(States(bob));
        Assert.Equal(0, rig.Relay.Stats.OnFootStatesDropped);
        Assert.True(rig.Relay.TryGetOnFoot(alice.PlayerId, out var stored));
        Assert.Equal(matched, stored!.ContainerNetId);
    }

    [Fact]
    public async Task APositionOutsideTheValidationRangeIsDropped()
    {
        await using var rig = new RelayRig();
        var authority = await rig.JoinAuthorityAsync();
        await SpawnStationAsync(rig, authority, Station);
        var alice = await rig.JoinInGameAsync("Alice", caps: OnFootCap);
        var bob = await rig.JoinInGameAsync("Bob", caps: OnFootCap);

        await rig.SendAsync(bob, MsgType.OnFootState, RelayFrames.OnFoot(1, Station));
        await rig.SendAsync(alice, MsgType.OnFootState, RelayFrames.OnFoot(1, Station, cx: 2001 * 64));

        Assert.Empty(States(bob));
        Assert.Equal(1, rig.Relay.Stats.OnFootStatesDropped);
    }

    [Fact]
    public async Task AFloodIsRateLimited()
    {
        await using var rig = new RelayRig(new RelayOptions { OnFootRelayHz = 1, OnFootBurst = 5 });
        var authority = await rig.JoinAuthorityAsync();
        await SpawnStationAsync(rig, authority, Station);
        var alice = await rig.JoinInGameAsync("Alice", caps: OnFootCap);
        var bob = await rig.JoinInGameAsync("Bob", caps: OnFootCap);

        await rig.SendAsync(bob, MsgType.OnFootState, RelayFrames.OnFoot(1, Station));
        for (uint seq = 1; seq <= 10; seq++)
        {
            await rig.SendAsync(alice, MsgType.OnFootState, RelayFrames.OnFoot(seq, Station));
        }

        Assert.Equal(5, States(bob).Count);
        Assert.Equal(5, rig.Relay.Stats.OnFootStatesRateLimited);
    }

    [Fact]
    public async Task AnOlderSequenceNumberIsIgnored()
    {
        await using var rig = new RelayRig();
        var authority = await rig.JoinAuthorityAsync();
        await SpawnStationAsync(rig, authority, Station);
        var alice = await rig.JoinInGameAsync("Alice", caps: OnFootCap);
        var bob = await rig.JoinInGameAsync("Bob", caps: OnFootCap);

        await rig.SendAsync(bob, MsgType.OnFootState, RelayFrames.OnFoot(1, Station));
        await rig.SendAsync(alice, MsgType.OnFootState, RelayFrames.OnFoot(5, Station));
        await rig.SendAsync(alice, MsgType.OnFootState, RelayFrames.OnFoot(4, Station));

        Assert.Single(States(bob));
        Assert.Equal(1, rig.Relay.Stats.OnFootStatesStale);
    }

    [Fact]
    public async Task ARestartedClientMayStartItsSequenceOverAfterAReload()
    {
        await using var rig = new RelayRig();
        var authority = await rig.JoinAuthorityAsync();
        await SpawnStationAsync(rig, authority, Station);
        var alice = await rig.JoinInGameAsync("Alice", caps: OnFootCap);
        var bob = await rig.JoinInGameAsync("Bob", caps: OnFootCap);

        await rig.SendAsync(bob, MsgType.OnFootState, RelayFrames.OnFoot(1, Station));
        await rig.SendAsync(alice, MsgType.OnFootState, RelayFrames.OnFoot(3903, Station));
        int walkingBefore = States(bob).Count(s => s.Mode == OnFootMode.Walking);

        // /reloadui: the extension process restarts and its seq starts at 1 again, but the server keeps the flow across the detach.
        // The detach itself sends one leave state (mode None) to the peers, so count the walking frames only.
        await rig.Rig.DisconnectAsync(alice, DisconnectCode.ClientReload);
        var back = await rig.Rig.ResumeAsync("Alice", alice);
        await rig.Actor.FlushAsync();

        await rig.SendAsync(back, MsgType.OnFootState, RelayFrames.OnFoot(1, Station));

        Assert.Equal(0, rig.Relay.Stats.OnFootStatesStale);
        var walking = States(bob).Where(s => s.Mode == OnFootMode.Walking).ToList();
        Assert.Equal(walkingBefore + 1, walking.Count);
        Assert.Equal((ushort)alice.PlayerId, walking[^1].PlayerId);
        Assert.Equal(1u, walking[^1].Seq);
    }

    // ------------------------------------------------------------------ roster

    [Fact]
    public async Task TheRosterIsBroadcastAtMostOncePerSecond()
    {
        await using var rig = new RelayRig();
        var authority = await rig.JoinAuthorityAsync();
        await SpawnStationAsync(rig, authority, Station);
        await SpawnStationAsync(rig, authority, OtherStation);
        var alice = await rig.JoinInGameAsync("Alice", caps: OnFootCap);
        var bob = await rig.JoinInGameAsync("Bob", caps: OnFootCap);

        await rig.SendAsync(bob, MsgType.OnFootState, RelayFrames.OnFoot(1, Station));
        await rig.SendAsync(alice, MsgType.OnFootState, RelayFrames.OnFoot(1, Station));
        await rig.AdvanceAsync(0.3);
        int afterFirst = Rosters(bob).Count;
        Assert.True(afterFirst > 0, "no roster was sent");
        var entry = Assert.Single(Rosters(bob).Last().Entries, e => e.PlayerId == alice.PlayerId);
        Assert.Equal(Station, entry.ContainerNetId);
        Assert.Equal(OnFootMode.Walking, entry.Mode);

        await rig.SendAsync(alice, MsgType.OnFootState, RelayFrames.OnFoot(2, OtherStation));
        await rig.AdvanceAsync(0.3);
        Assert.Equal(afterFirst, Rosters(bob).Count); // inside the 1 Hz slot

        await rig.AdvanceAsync(1.0);
        Assert.True(Rosters(bob).Count > afterFirst);
        Assert.Equal(OtherStation, Rosters(bob).Last().Entries.Single(e => e.PlayerId == alice.PlayerId).ContainerNetId);
    }

    [Fact]
    public async Task TheRosterDropsAPlayerWhoLeavesTheFoot()
    {
        await using var rig = new RelayRig();
        var authority = await rig.JoinAuthorityAsync();
        await SpawnStationAsync(rig, authority, Station);
        var alice = await rig.JoinInGameAsync("Alice", caps: OnFootCap);
        var bob = await rig.JoinInGameAsync("Bob", caps: OnFootCap);

        await rig.SendAsync(bob, MsgType.OnFootState, RelayFrames.OnFoot(1, Station));
        await rig.SendAsync(alice, MsgType.OnFootState, RelayFrames.OnFoot(1, Station));
        await rig.AdvanceAsync(0.3);
        Assert.Contains(Rosters(bob).Last().Entries, e => e.PlayerId == alice.PlayerId);

        await rig.SendAsync(alice, MsgType.OnFootState, RelayFrames.OnFoot(2, 0, mode: OnFootMode.None));
        await rig.AdvanceAsync(1.2);
        Assert.DoesNotContain(Rosters(bob).Last().Entries, e => e.PlayerId == alice.PlayerId);
    }

    // ------------------------------------------------------------------ appearance

    [Fact]
    public async Task AppearanceIsStampedStoredAndRelayedWithAMonotonicRevision()
    {
        await using var rig = new RelayRig();
        var authority = await rig.JoinAuthorityAsync();
        var alice = await rig.JoinInGameAsync("Alice", caps: OnFootCap);
        var bob = await rig.JoinInGameAsync("Bob", caps: OnFootCap);

        await rig.SendAsync(alice, MsgType.PlayerAppearance, RelayFrames.Appearance(claimedPlayerId: 99, claimedRevision: 500));
        await rig.SendAsync(alice, MsgType.PlayerAppearance, RelayFrames.Appearance(preset: "female_b", macro: ""));

        var received = Appearances(bob);
        Assert.Equal(2, received.Count);
        Assert.All(received, a => Assert.Equal(alice.PlayerId, a.PlayerId));
        Assert.Equal([1u, 2u], received.Select(a => a.Revision));
        Assert.Equal("female_b", received[1].Preset);

        var stored = rig.Relay.GetAppearance(alice.PlayerId);
        Assert.NotNull(stored);
        Assert.Equal(2u, stored!.Revision);
        Assert.Equal(2, rig.Relay.Stats.AppearancesRelayed);
    }

    [Fact]
    public async Task AJoiningNodeGetsTheAppearancesTheServerAlreadyHolds()
    {
        await using var rig = new RelayRig();
        var authority = await rig.JoinAuthorityAsync();
        var alice = await rig.JoinInGameAsync("Alice", caps: OnFootCap);
        await rig.SendAsync(alice, MsgType.PlayerAppearance, RelayFrames.Appearance(race: "teladi", preset: "male_c"));

        var bob = await rig.JoinInGameAsync("Bob", caps: OnFootCap);

        var received = Assert.Single(Appearances(bob), a => a.PlayerId == alice.PlayerId);
        Assert.Equal("teladi", received.Race);
        Assert.Equal("male_c", received.Preset);
    }

    // ------------------------------------------------------------------ interactions

    [Fact]
    public async Task AnInteractionGoesOnlyToTheTarget()
    {
        await using var rig = new RelayRig();
        var authority = await rig.JoinAuthorityAsync();
        var alice = await rig.JoinInGameAsync("Alice", caps: OnFootCap);
        var bob = await rig.JoinInGameAsync("Bob", caps: OnFootCap);
        var carol = await rig.JoinInGameAsync("Carol", caps: OnFootCap);

        await rig.SendAsync(alice, MsgType.PlayerInteraction, RelayFrames.Interaction((ushort)bob.PlayerId, InteractionKind.Wave, claimedFrom: 99));

        var received = Assert.Single(Interactions(bob));
        Assert.Equal(alice.PlayerId, received.FromPlayer);
        Assert.Equal(bob.PlayerId, received.ToPlayer);
        Assert.Equal(InteractionKind.Wave, received.Kind);
        Assert.Empty(Interactions(carol));
        Assert.Empty(Interactions(authority));
        Assert.Equal(1, rig.Relay.Stats.InteractionsRelayed);
    }

    [Fact]
    public async Task AnInteractionToAnUnknownPlayerIsRejected()
    {
        await using var rig = new RelayRig();
        await rig.JoinAuthorityAsync();
        var alice = await rig.JoinInGameAsync("Alice", caps: OnFootCap);

        await rig.SendAsync(alice, MsgType.PlayerInteraction, RelayFrames.Interaction(999));

        Assert.Equal(1, rig.Relay.Stats.InteractionsRejected);
        Assert.Equal(0, rig.Relay.Stats.InteractionsRelayed);
    }

    [Fact]
    public async Task AnInteractionToASelfIsRejected()
    {
        await using var rig = new RelayRig();
        await rig.JoinAuthorityAsync();
        var alice = await rig.JoinInGameAsync("Alice", caps: OnFootCap);

        await rig.SendAsync(alice, MsgType.PlayerInteraction, RelayFrames.Interaction((ushort)alice.PlayerId));

        Assert.Equal(1, rig.Relay.Stats.InteractionsRejected);
        Assert.Empty(Interactions(alice));
    }
}

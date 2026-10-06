using X4MP.Core.Relay;
using X4MP.Core.Session;
using X4MP.Core.Tests.Session;
using X4MP.Core.Tests.World;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.Core.Tests.Relay;

/// <summary>M3-01: avatars that outlive their player (parked, Q6), the roster's <c>online</c> flag, held requests across an authority resume, admin removal.</summary>
public class RelayAvatarTests
{
    private static List<PlayerShipT> Ships(JoinedNode node) =>
        [.. node.Connection.SentOf(MsgType.PlayerShip).Select(f => f.Decode<PlayerShip>().UnPack())];

    private static List<PlayerInfoT> RosterOf(JoinedNode node, int playerId) =>
        [.. node.Connection.SentOf(MsgType.RosterUpdate)
            .SelectMany(f => f.Decode<RosterUpdate>().UnPack().Players)
            .Where(p => p.PlayerId == playerId)];

    private static List<DespawnEntryT> Despawns(JoinedNode node) =>
        [.. node.Connection.SentOf(MsgType.EntityDespawn).SelectMany(f => f.Decode<EntityDespawn>().UnPack().Entries)];

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

    private static byte[] AvatarSpawn(JoinedNode owner, uint netId = 500, ushort controller = 0) =>
        WorldKit.SpawnPayload(WorldKit.Rec(
            netId, EntityKind.ShipS, 1, px: 640, ownerTeam: 1, ownerPlayer: (ushort)owner.PlayerId,
            controller: controller == 0 ? (ushort)owner.PlayerId : controller, origin: EntityOrigin.PlayerShip, name: "Pilot"));

    // ------------------------------------------------------------------ held PlayerShip requests

    [Fact]
    public async Task APlayerShipRequestSentWhileTheAuthorityIsGoneIsForwardedWhenItResumes()
    {
        await using var rig = new RelayRig();
        var authority = await rig.JoinAuthorityAsync();
        var alice = await rig.JoinInGameAsync("Alice");

        await DropAsync(rig, authority);
        await rig.SendAsync(alice, MsgType.PlayerShip, RelayFrames.PlayerShip(key: 11));
        Assert.Empty(Ships(authority)); // nobody to forward to: the request is held

        var back = await rig.Rig.ResumeAsync("Boss", authority);
        Assert.True(back.Welcome.Resumed);
        await rig.Actor.FlushAsync();

        var request = Assert.Single(Ships(back));
        Assert.Equal(11ul, request.RequestKey.Lo);
        Assert.Equal((ushort)alice.PlayerId, request.PlayerId);
    }

    [Fact]
    public async Task ARequestTheAuthorityMayHaveLostWithItsOldSocketIsSentAgainAfterTheResume()
    {
        await using var rig = new RelayRig();
        var authority = await rig.JoinAuthorityAsync();
        var alice = await rig.JoinInGameAsync("Alice");
        await rig.SendAsync(alice, MsgType.PlayerShip, RelayFrames.PlayerShip(key: 12));
        Assert.Single(Ships(authority)); // forwarded once, then the authority's socket died before it answered

        await DropAsync(rig, authority);
        var back = await rig.Rig.ResumeAsync("Boss", authority);
        await rig.Actor.FlushAsync();

        Assert.Equal(12ul, Assert.Single(Ships(back)).RequestKey.Lo); // the authority returns the existing avatar for a repeat
    }

    [Fact]
    public async Task AnAnsweredRequestIsNotSentAgainAfterAnAuthorityResume()
    {
        await using var rig = new RelayRig();
        var authority = await rig.JoinAuthorityAsync();
        var alice = await rig.JoinInGameAsync("Alice");
        await rig.SendAsync(alice, MsgType.PlayerShip, RelayFrames.PlayerShip(key: 13));
        await rig.SendPayloadAsync(authority, MsgType.EntitySpawn, AvatarSpawn(alice));

        await DropAsync(rig, authority);
        var back = await rig.Rig.ResumeAsync("Boss", authority);
        await rig.Actor.FlushAsync();

        Assert.Empty(Ships(back));
    }

    // ------------------------------------------------------------------ M3-29: a world rollback strands a flying player

    [Fact]
    public async Task ARollbackThatRemovesAnOnlinePlayersAvatarSendsThePlayerShipAgainToTheNextAuthorityWithTheNewestPoseExactlyOnce()
    {
        await using var rig = new RelayRig();
        var authority = await rig.JoinAuthorityAsync();
        var alice = await rig.JoinInGameAsync("Alice");
        var bob = await rig.JoinInGameAsync("Bob");
        await rig.SendAsync(alice, MsgType.PlayerShip, RelayFrames.PlayerShip(key: 21));
        await rig.SendAsync(bob, MsgType.PlayerShip, RelayFrames.PlayerShip(key: 22));
        await rig.SendPayloadAsync(authority, MsgType.EntitySpawn, AvatarSpawn(alice, netId: 600)); // spawned after the checkpoint: rolled back
        await rig.SendPayloadAsync(authority, MsgType.EntitySpawn, AvatarSpawn(bob, netId: 400)); // in the checkpoint: survives
        Assert.Equal(2, Ships(authority).Count);

        // Alice keeps flying on her own PC
        await rig.SendAsync(alice, MsgType.PlayerState, RelayFrames.State(1, 0, px: 64_000, sector: 9));

        await DropAsync(rig, authority);
        int removed = rig.Mirror.RollbackToNetIdFloor(500, authority.PlayerId);
        Assert.Equal(1, removed);
        Assert.False(rig.Mirror.TryGet(600, out _));
        Assert.True(rig.Mirror.TryGet(400, out _));

        var back = await rig.Rig.ResumeAsync("Boss", authority);
        await rig.Actor.FlushAsync();

        var request = Assert.Single(Ships(back)); // Alice only: Bob's avatar is still there
        Assert.Equal((ushort)alice.PlayerId, request.PlayerId);
        Assert.Equal(64_000, request.Px);
        Assert.Equal((ushort)9, request.Sector);
        Assert.Equal(1L, rig.Relay.Stats.AvatarsReprovisioned);

        // the new avatar arrives: nothing is sent again, not even after another resume
        await rig.SendPayloadAsync(back, MsgType.EntitySpawn, AvatarSpawn(alice, netId: 510));
        await DropAsync(rig, back);
        var again = await rig.Rig.ResumeAsync("Boss", back);
        await rig.Actor.FlushAsync();
        Assert.Empty(Ships(again));
        Assert.Equal(1L, rig.Relay.Stats.AvatarsReprovisioned);
    }

    [Fact]
    public async Task AnAdminRemovalOrALeavingPlayerIsNeverReprovisioned()
    {
        await using var rig = new RelayRig();
        var authority = await rig.JoinAuthorityAsync();
        var alice = await rig.JoinInGameAsync("Alice");
        var bob = await rig.JoinInGameAsync("Bob");
        await rig.SendAsync(alice, MsgType.PlayerShip, RelayFrames.PlayerShip(key: 31));
        await rig.SendAsync(bob, MsgType.PlayerShip, RelayFrames.PlayerShip(key: 32));
        await rig.SendPayloadAsync(authority, MsgType.EntitySpawn, AvatarSpawn(alice, netId: 600));
        await rig.SendPayloadAsync(authority, MsgType.EntitySpawn, AvatarSpawn(bob, netId: 601));

        await rig.Relay.RemoveAvatarsAsync(alice.PlayerId); // admin kick option
        await rig.Rig.DisconnectAsync(bob, DisconnectCode.ClientQuit); // Bob leaves, his parked avatar stays
        await rig.Actor.FlushAsync();
        rig.Mirror.RollbackToNetIdFloor(500, authority.PlayerId);

        await DropAsync(rig, authority);
        var back = await rig.Rig.ResumeAsync("Boss", authority);
        await rig.Actor.FlushAsync();
        Assert.Empty(Ships(back));
        Assert.Equal(0L, rig.Relay.Stats.AvatarsReprovisioned);
    }

    [Fact]
    public async Task APlayerStateNamingARolledBackAvatarIsFedToTheAuthorityWithoutThatNetId()
    {
        await using var rig = new RelayRig();
        var authority = await rig.JoinAuthorityAsync();
        var alice = await rig.JoinInGameAsync("Alice");
        await rig.SendAsync(alice, MsgType.PlayerShip, RelayFrames.PlayerShip(key: 41));
        await rig.SendPayloadAsync(authority, MsgType.EntitySpawn, AvatarSpawn(alice, netId: 600));
        rig.Mirror.RollbackToNetIdFloor(500, authority.PlayerId);

        await rig.AdvanceAsync(1);
        await rig.SendAsync(alice, MsgType.PlayerState, RelayFrames.State(5, 5_000_000, px: 100, netId: 600)); // the client still names the old id
        await rig.AdvanceAsync(1);

        var fed = authority.Connection.SentOf(MsgType.PlayerState)
            .Select(f => MessageRegistry.Default.Decode<PlayerState>(new Frame(f.Type, FrameOptions.None, Lane.Realtime, f.Payload)).UnPack()).ToList();
        Assert.NotEmpty(fed);
        Assert.All(fed, s => Assert.Equal(0u, s.NetId));
    }

    // ------------------------------------------------------------------ parked avatars (Q6)

    [Fact]
    public async Task WhenTheAuthorityClearsTheControllerTheAvatarStaysInTheMirrorAsAParkedShipAndTheRosterDropsItsShip()
    {
        await using var rig = new RelayRig();
        var authority = await rig.JoinAuthorityAsync();
        var alice = await rig.JoinInGameAsync("Alice");
        var bob = await rig.JoinInGameAsync("Bob");
        await rig.SendPayloadAsync(authority, MsgType.EntitySpawn, AvatarSpawn(alice));
        Assert.Equal(500u, RosterOf(bob, alice.PlayerId)[^1].ShipNetId);

        await rig.SendPayloadAsync(authority, MsgType.EntityChange, MessageEncoderHelper.Change(500, ChangeField.Controller, 0));

        Assert.True(rig.Mirror.TryGet(500, out var ship));
        Assert.Equal((ushort)0, ship.ControllerPlayer);
        Assert.True(ship.IsPlayerShip); // origin PlayerShip: still a player ship, replicated galaxy-wide
        Assert.Equal(500u, Assert.Single(rig.Mirror.ParkedAvatars).NetId);
        Assert.Equal(0u, RosterOf(bob, alice.PlayerId)[^1].ShipNetId);
        Assert.Empty(Despawns(bob)); // nothing removes it
    }

    [Fact]
    public async Task APlayerWhoLeavesLeavesItsAvatarInTheMirror()
    {
        await using var rig = new RelayRig();
        var authority = await rig.JoinAuthorityAsync();
        var alice = await rig.JoinInGameAsync("Alice");
        await rig.SendPayloadAsync(authority, MsgType.EntitySpawn, AvatarSpawn(alice));

        await rig.Rig.DisconnectAsync(alice, DisconnectCode.ClientQuit);
        await rig.Actor.FlushAsync();
        await rig.SendPayloadAsync(authority, MsgType.EntityChange, MessageEncoderHelper.Change(500, ChangeField.Controller, 0));

        Assert.Equal(1, rig.Mirror.AvatarCount);
        Assert.Equal(500u, Assert.Single(rig.Mirror.ParkedAvatars).NetId);
        Assert.Equal(0, rig.Mirror.PlayerShipCount); // the player's own state is gone with the node
    }

    [Fact]
    public async Task ARejoiningPlayerGetsItsParkedAvatarBackThroughAControllerChange()
    {
        await using var rig = new RelayRig();
        var authority = await rig.JoinAuthorityAsync();
        var alice = await rig.JoinInGameAsync("Alice");
        var bob = await rig.JoinInGameAsync("Bob");
        await rig.SendPayloadAsync(authority, MsgType.EntitySpawn, AvatarSpawn(alice));
        await rig.SendPayloadAsync(authority, MsgType.EntityChange, MessageEncoderHelper.Change(500, ChangeField.Controller, 0));
        Assert.Equal(0u, RosterOf(bob, alice.PlayerId)[^1].ShipNetId);

        // the authority answers Alice's PlayerShip with a change, not a new spawn
        await rig.SendPayloadAsync(authority, MsgType.EntityChange, MessageEncoderHelper.Change(500, ChangeField.Controller, (ushort)alice.PlayerId));

        Assert.Equal(500u, RosterOf(bob, alice.PlayerId)[^1].ShipNetId);
        Assert.Empty(rig.Mirror.ParkedAvatars);
        Assert.Equal(2, rig.Events.OfType<PlayerShipAssigned>().ToList().Count);
    }

    // ------------------------------------------------------------------ roster online flag

    [Fact]
    public async Task ALostSocketShowsAsOnlineFalseInTheRosterAndTheResumeShowsItBack()
    {
        await using var rig = new RelayRig();
        await rig.JoinAuthorityAsync();
        var alice = await rig.JoinInGameAsync("Alice");
        var bob = await rig.JoinInGameAsync("Bob");
        Assert.True(RosterOf(bob, alice.PlayerId)[^1].Online);

        await DropAsync(rig, alice);
        Assert.False(RosterOf(bob, alice.PlayerId)[^1].Online);

        await rig.Rig.ResumeAsync("Alice", alice);
        await rig.Actor.FlushAsync();
        Assert.True(RosterOf(bob, alice.PlayerId)[^1].Online);
    }

    [Fact]
    public async Task APlannedReloadStaysInvisibleInTheRoster()
    {
        await using var rig = new RelayRig();
        await rig.JoinAuthorityAsync();
        var alice = await rig.JoinInGameAsync("Alice");
        var bob = await rig.JoinInGameAsync("Bob");
        int before = bob.Connection.SentOf(MsgType.RosterUpdate).Count;

        await rig.Rig.DisconnectAsync(alice, DisconnectCode.ClientReload);
        await rig.Rig.ResumeAsync("Alice", alice);
        await rig.Actor.FlushAsync();

        Assert.Equal(before, bob.Connection.SentOf(MsgType.RosterUpdate).Count);
    }

    // ------------------------------------------------------------------ admin removal

    [Fact]
    public async Task RemovingAPlayersAvatarsDropsThemFromTheMirrorAndOrdersTheAuthorityToRemoveThem()
    {
        await using var rig = new RelayRig();
        var authority = await rig.JoinAuthorityAsync();
        var alice = await rig.JoinInGameAsync("Alice");
        var bob = await rig.JoinInGameAsync("Bob");
        await rig.SendPayloadAsync(authority, MsgType.EntitySpawn, AvatarSpawn(alice, 500));
        await rig.SendPayloadAsync(authority, MsgType.EntitySpawn, AvatarSpawn(bob, 501));
        await rig.SendPayloadAsync(authority, MsgType.EntityChange, MessageEncoderHelper.Change(500, ChangeField.Controller, 0)); // Alice's is parked

        var removed = await rig.Relay.RemoveAvatarsAsync(alice.PlayerId);

        Assert.Equal([500u], removed);
        Assert.False(rig.Mirror.TryGet(500, out _));
        Assert.True(rig.Mirror.TryGet(501, out _)); // Bob's avatar is untouched
        var order = Assert.Single(Despawns(authority));
        Assert.Equal(500u, order.NetId);
        Assert.Equal(DespawnReason.Removed, order.Reason);
        Assert.Equal(1, rig.Relay.Stats.AvatarsRemoved);
    }

    [Fact]
    public async Task RemovingAnAvatarOfAPlayerWhoAlreadyLeftWorksAndDropsAWaitingRequest()
    {
        await using var rig = new RelayRig();
        var authority = await rig.JoinAuthorityAsync();
        var alice = await rig.JoinInGameAsync("Alice");
        await rig.SendPayloadAsync(authority, MsgType.EntitySpawn, AvatarSpawn(alice));
        await rig.Rig.DisconnectAsync(alice, DisconnectCode.ClientQuit);
        await rig.Actor.FlushAsync();

        var removed = await rig.Relay.RemoveAvatarsAsync(alice.PlayerId);

        Assert.Equal([500u], removed);
        Assert.Equal(0, rig.Mirror.AvatarCount);
        Assert.Empty(await rig.Relay.RemoveAvatarsAsync(alice.PlayerId)); // idempotent
    }
}

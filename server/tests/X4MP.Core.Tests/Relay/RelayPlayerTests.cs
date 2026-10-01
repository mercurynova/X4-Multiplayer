using X4MP.Core.Relay;
using X4MP.Core.Tests.Session;
using X4MP.Core.Tests.World;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.Core.Tests.Relay;

/// <summary>PlayerState derivation and feed, and the PlayerShip avatar flow.</summary>
public class RelayPlayerTests
{
    private static List<PlayerStateT> States(JoinedNode node) =>
        [.. node.Connection.SentOf(MsgType.PlayerState)
            .Select(f => MessageRegistry.Default.Decode<PlayerState>(new Frame(f.Type, FrameOptions.None, Lane.Realtime, f.Payload)).UnPack())];

    private static List<PlayerShipT> Ships(JoinedNode node) =>
        [.. node.Connection.SentOf(MsgType.PlayerShip).Select(f => f.Decode<PlayerShip>().UnPack())];

    private static List<PlayerInfoT> RosterOf(JoinedNode node, int playerId) =>
        [.. node.Connection.SentOf(MsgType.RosterUpdate)
            .SelectMany(f => f.Decode<RosterUpdate>().UnPack().Players)
            .Where(p => p.PlayerId == playerId)];

    [Fact]
    public async Task TheVelocityIsTheThreeSampleSlopeOfThePositions()
    {
        await using var rig = new RelayRig();
        await rig.JoinAuthorityAsync();
        var a = await rig.JoinInGameAsync("Alice");

        // 10 m every 50 ms is 200 m/s along x
        for (uint i = 0; i < 3; i++)
        {
            await rig.SendAsync(a, MsgType.PlayerState, RelayFrames.State(i + 1, i * 50_000UL, px: (int)(i * 640)));
        }

        Assert.True(rig.Relay.TryGetVelocity(a.PlayerId, out var v));
        Assert.Equal(200.0, v.X, 6);
        Assert.Equal(0.0, v.Y, 6);
        Assert.Equal(0.0, v.Z, 6);
    }

    [Fact]
    public async Task TheDerivedVelocityIsWrittenIntoTheMirrorEntityOfTheBoundAvatar()
    {
        await using var rig = new RelayRig();
        var authority = await rig.JoinAuthorityAsync();
        var a = await rig.JoinInGameAsync("Alice");
        await rig.SendPayloadAsync(authority, MsgType.EntitySpawn, WorldKit.SpawnPayload(
            WorldKit.Rec(500, EntityKind.ShipS, 1, controller: (ushort)a.PlayerId, origin: EntityOrigin.PlayerShip)));

        for (uint i = 0; i < 3; i++)
        {
            await rig.SendAsync(a, MsgType.PlayerState, RelayFrames.State(i + 1, i * 50_000UL, px: (int)(i * 640), netId: 500));
        }

        Assert.True(rig.Mirror.TryGet(500, out var ship));
        Assert.Equal(800, ship.Vx); // 200 m/s at 0.25 m/s per count
        Assert.Equal(0, ship.Vy);
        Assert.Equal(1280, ship.Px);
    }

    [Fact]
    public async Task StatesGoToTheAuthorityAtMostTwentyTimesASecondAndTheNewestWins()
    {
        await using var rig = new RelayRig();
        var authority = await rig.JoinAuthorityAsync();
        var a = await rig.JoinInGameAsync("Alice");
        var b = await rig.JoinInGameAsync("Bob");

        // a burst inside one 50 ms slot: the first goes out, the rest collapse into the newest
        for (uint i = 1; i <= 5; i++)
        {
            await rig.SendAsync(a, MsgType.PlayerState, RelayFrames.State(i, i * 1000UL, px: (int)(i * 64)));
        }

        Assert.Equal([1u], States(authority).Select(s => s.Seq));

        await rig.AdvanceAsync(0.06);

        Assert.Equal([1u, 5u], States(authority).Select(s => s.Seq));
        Assert.Equal(a.PlayerId, a.PlayerId);
        Assert.Empty(States(b)); // nobody else gets a raw PlayerState: others see the ship through replication
        Assert.Equal(3, rig.Relay.Stats.StatesSuperseded);

        // one second of 100 Hz input is fed at 20 Hz
        int before = States(authority).Count;
        for (uint i = 0; i < 100; i++)
        {
            await rig.SendAsync(a, MsgType.PlayerState, RelayFrames.State(100 + i, 100_000UL + (i * 10_000), px: (int)(i * 64)));
            await rig.AdvanceAsync(0.01);
        }

        int fed = States(authority).Count - before;
        Assert.InRange(fed, 18, 22);
        Assert.Equal(199u, States(authority)[^1].Seq); // the newest was not lost
    }

    [Fact]
    public async Task TheAuthoritysOwnStateIsNotFedBack()
    {
        await using var rig = new RelayRig();
        var authority = await rig.JoinAuthorityAsync();
        await rig.SendAsync(authority, MsgType.PlayerState, RelayFrames.State(1, 0, px: 10));

        Assert.Empty(States(authority));
        Assert.Equal(1, rig.Mirror.PlayerShipCount); // the mirror still has it
    }

    [Fact]
    public async Task PlayerShipGoesToTheAuthorityAndTheAvatarSpawnReachesTheRosterAndTheStates()
    {
        await using var rig = new RelayRig();
        var authority = await rig.JoinAuthorityAsync();
        var a = await rig.JoinInGameAsync("Alice");
        var b = await rig.JoinInGameAsync("Bob");

        await rig.SendAsync(a, MsgType.PlayerShip, RelayFrames.PlayerShip(key: 5));

        var request = Assert.Single(Ships(authority));
        Assert.Equal(5ul, request.RequestKey.Lo);
        Assert.Equal((ushort)a.PlayerId, request.PlayerId); // the server says whose avatar it is
        Assert.Equal(0ul, request.RequestKey.Hi); // request_key stays a pure idempotency key
        Assert.Equal("ship_arg_s_fighter_01_a_macro", request.ShipMacro);
        Assert.Empty(Ships(b));

        await rig.SendPayloadAsync(authority, MsgType.EntitySpawn, WorldKit.SpawnPayload(
            WorldKit.Rec(500, EntityKind.ShipS, 1, ownerTeam: 1, ownerPlayer: (ushort)a.PlayerId, controller: (ushort)a.PlayerId, origin: EntityOrigin.PlayerShip)));

        foreach (var node in new[] { a, b, authority })
        {
            Assert.Equal(500u, RosterOf(node, a.PlayerId)[^1].ShipNetId);
        }

        var assigned = Assert.Single(rig.Events.OfType<PlayerShipAssigned>());
        Assert.Equal(500, assigned.ShipNetId);
        Assert.Equal(a.PlayerId, assigned.PlayerId);

        // the client does not know its id yet; the server stamps it on the way to the authority
        await rig.SendAsync(a, MsgType.PlayerState, RelayFrames.State(1, 0, px: 64, netId: 0));
        Assert.Equal(500u, States(authority)[^1].NetId);

        await rig.SendPayloadAsync(authority, MsgType.EntityDespawn, WorldKit.DespawnPayload(DespawnReason.Destroyed, 0, 500));
        Assert.Equal(0u, RosterOf(b, a.PlayerId)[^1].ShipNetId);
    }

    [Fact]
    public async Task PlayerShipWaitsForTheAuthorityAndIsForwardedWhenItIsInGame()
    {
        await using var rig = new RelayRig();
        var a = await rig.JoinInGameAsync("Alice");

        await rig.SendAsync(a, MsgType.PlayerShip, RelayFrames.PlayerShip(key: 6));
        var authority = await rig.JoinAuthorityAsync();
        await rig.Actor.FlushAsync();

        var request = Assert.Single(Ships(authority));
        Assert.Equal(6ul, request.RequestKey.Lo);
        Assert.Equal((ushort)a.PlayerId, request.PlayerId);
    }

    [Fact]
    public async Task AClientSuppliedPlayerIdOnPlayerShipIsOverwrittenByTheServer()
    {
        await using var rig = new RelayRig();
        var authority = await rig.JoinAuthorityAsync();
        var mallory = await rig.JoinInGameAsync("Mallory");
        var victim = await rig.JoinInGameAsync("Victim");

        // Mallory claims to be the victim, in player_id and in the request key
        await rig.SendAsync(mallory, MsgType.PlayerShip, RelayFrames.PlayerShip(key: 9, claimedPlayerId: (ushort)victim.PlayerId, keyHi: (ulong)victim.PlayerId));

        var request = Assert.Single(Ships(authority));
        Assert.Equal((ushort)mallory.PlayerId, request.PlayerId);
        Assert.NotEqual((ushort)victim.PlayerId, request.PlayerId);
        Assert.Equal(9ul, request.RequestKey.Lo); // the idempotency key is passed through untouched
    }

    [Fact]
    public async Task ASecondPlayerShipRequestReplacesTheFirstWhileWaiting()
    {
        await using var rig = new RelayRig();
        var a = await rig.JoinInGameAsync("Alice");
        await rig.SendAsync(a, MsgType.PlayerShip, RelayFrames.PlayerShip(key: 1, macro: "first"));
        await rig.SendAsync(a, MsgType.PlayerShip, RelayFrames.PlayerShip(key: 2, macro: "second"));

        var authority = await rig.JoinAuthorityAsync();
        await rig.Actor.FlushAsync();

        Assert.Equal(["second"], Ships(authority).Select(s => s.ShipMacro));
    }
}

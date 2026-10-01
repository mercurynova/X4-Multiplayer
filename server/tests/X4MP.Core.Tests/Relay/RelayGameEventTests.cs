using System.Text.Json;
using X4MP.Core.Events;
using X4MP.Core.Tests.Session;
using X4MP.Proto;

namespace X4MP.Core.Tests.Relay;

/// <summary>Authority game events: fan-out by interest, sequence stamping, publication.</summary>
public class RelayGameEventTests
{
    private static List<GameEventT> Events(JoinedNode node) =>
        [.. node.Connection.SentOf(MsgType.GameEvent).Select(f => f.Decode<GameEvent>().UnPack())];

    [Fact]
    public async Task AnAuthorityEventReachesInterestedClientsAndTheEventLog()
    {
        var interest = new FakeInterest();
        await using var rig = new RelayRig(interest: interest);
        var authority = await rig.JoinAuthorityAsync();
        var a = await rig.JoinInGameAsync("Alice");
        var b = await rig.JoinInGameAsync("Bob");
        interest.Sectors.Add((a.PlayerId, 5));

        await rig.SendAsync(authority, MsgType.GameEvent, RelayFrames.GameEvent(5, RelayFrames.Kill(killerPlayer: (ushort)a.PlayerId)));

        var received = Assert.Single(Events(a));
        Assert.Equal(GameEventBody.KillEvent, received.Body.Type);
        Assert.Equal(4711u, received.Body.AsKillEvent().Victim);
        Assert.Equal(1ul, received.EventSeq); // the server numbers events
        Assert.Equal(5, received.Sector);
        Assert.Empty(Events(b)); // not following sector 5
        Assert.Empty(Events(authority)); // no echo to the sender

        var logged = Assert.Single(rig.Events.OfType<GameEventOccurred>());
        Assert.Equal("Kill", logged.Kind);
        Assert.Equal(5, logged.SectorId);
        Assert.Equal(a.PlayerId, logged.PlayerId);
        using var json = JsonDocument.Parse(logged.DataJson!);
        Assert.Equal(4711, json.RootElement.GetProperty("body").GetProperty("victim").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("seq").GetInt32());
    }

    [Fact]
    public async Task PlayerRelatedAndSectorlessEventsGoToEveryone()
    {
        var interest = new FakeInterest();
        await using var rig = new RelayRig(interest: interest);
        var authority = await rig.JoinAuthorityAsync();
        var a = await rig.JoinInGameAsync("Alice");
        var b = await rig.JoinInGameAsync("Bob");

        await rig.SendAsync(authority, MsgType.GameEvent, RelayFrames.GameEvent(9, RelayFrames.PlayerDied((ushort)a.PlayerId)));
        await rig.SendAsync(authority, MsgType.GameEvent, RelayFrames.GameEvent(0, RelayFrames.Kill()));

        Assert.Equal([1ul, 2ul], Events(a).Select(e => e.EventSeq));
        Assert.Equal([1ul, 2ul], Events(b).Select(e => e.EventSeq));
        Assert.Equal(2, rig.Events.OfType<GameEventOccurred>().Count);
    }

    [Fact]
    public async Task WithoutAnInterestSourceEveryLiveNodeGetsTheEvent()
    {
        await using var rig = new RelayRig();
        var authority = await rig.JoinAuthorityAsync();
        var a = await rig.JoinInGameAsync("Alice");

        await rig.SendAsync(authority, MsgType.GameEvent, RelayFrames.GameEvent(3, RelayFrames.Kill()));

        Assert.Single(Events(a));
    }

    [Fact]
    public async Task AnEventWithoutABodyIsDropped()
    {
        await using var rig = new RelayRig();
        var authority = await rig.JoinAuthorityAsync();
        var a = await rig.JoinInGameAsync("Alice");

        await rig.SendAsync(authority, MsgType.GameEvent, RelayFrames.GameEvent(3, new GameEventBodyUnion()));

        Assert.Empty(Events(a));
        Assert.Empty(rig.Events.OfType<GameEventOccurred>());
    }
}

using Google.FlatBuffers;
using Microsoft.Extensions.Time.Testing;
using X4MP.Core.Session;
using X4MP.Core.Teams;
using X4MP.Core.Tests.Session;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.Core.Tests.Teams;

/// <summary>An <see cref="ITeamStore"/> in memory: remembers every save, loads the latest one.</summary>
public sealed class MemoryTeamStore : ITeamStore
{
    public List<(long Session, TeamStateSnapshot Snapshot)> Saves { get; } = [];

    public TeamStateSnapshot? Latest => Saves.Count == 0 ? null : Saves[^1].Snapshot;

    public TeamStateSnapshot? LoadLatest() => Latest;

    public bool Save(long sessionId, TeamStateSnapshot snapshot)
    {
        lock (Saves)
        {
            Saves.Add((sessionId, snapshot));
        }

        return true;
    }
}

/// <summary>The actor wired to a <see cref="TeamModule"/> (first in line) and a recording module behind it.</summary>
public sealed class TeamRig : IAsyncDisposable
{
    public TeamRig(TeamOptions? options = null, MemoryTeamStore? store = null)
    {
        Options = options ?? new TeamOptions();
        Store = store ?? new MemoryTeamStore();
        Time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
        Teams = new TeamModule(() => Options, Store, Time);
        Recorder = new RecordingModule();
        Rig = new ActorRig(Time, null, null, [Teams, Recorder]);
        Teams.Changed += change => Versions.Add(change.Version);
    }

    public TeamOptions Options { get; }

    public MemoryTeamStore Store { get; }

    public FakeTimeProvider Time { get; }

    public TeamModule Teams { get; }

    public RecordingModule Recorder { get; }

    public ActorRig Rig { get; }

    public List<int> Versions { get; } = [];

    public Task<JoinedNode> JoinAsync(string name, Role roles = Role.Client) => Rig.JoinAsync(name, roles);

    /// <summary>Joins <paramref name="count"/> clients named C1..Cn.</summary>
    public async Task<List<JoinedNode>> JoinManyAsync(int count)
    {
        var nodes = new List<JoinedNode>();
        for (int i = 1; i <= count; i++)
        {
            nodes.Add(await JoinAsync("C" + i));
        }

        await SettleAsync();
        return nodes;
    }

    /// <summary>Lets queued node triggers (which the module posts behind the current input) run.</summary>
    public async Task SettleAsync()
    {
        await Rig.Actor.FlushAsync();
        await Rig.Actor.FlushAsync();
    }

    public async Task AdvanceSecondsAsync(double seconds)
    {
        await Rig.AdvanceSecondsAsync(seconds);
        await SettleAsync();
    }

    public async Task<NodePhase> PhaseAsync(JoinedNode node) => (await Rig.NodeAsync(node.PlayerId)).Phase;

    /// <summary>Waits until the node reaches <paramref name="phase"/> (a dropped socket is noticed asynchronously).</summary>
    public async Task WaitForPhaseAsync(JoinedNode node, NodePhase phase)
    {
        long until = Environment.TickCount64 + 5000;
        while (await PhaseAsync(node) != phase)
        {
            Assert.True(Environment.TickCount64 < until, $"the node never reached {phase}");
            await Task.Delay(1);
        }
    }

    public async Task<TeamRequestResultT> ChooseAsync(JoinedNode node, int teamId, ulong requestKey = 0, byte[]? password = null)
    {
        int before = node.Connection.SentOf(MsgType.TeamRequestResult).Count;
        await Rig.SendAsync(node, MsgType.TeamChoice, TeamFrames.Choice(requestKey == 0 ? NextKey() : requestKey, teamId, password));
        await SettleAsync();
        return LastResult(node, before);
    }

    public async Task<TeamRequestResultT> CreateAsync(JoinedNode node, string name, ulong requestKey = 0)
    {
        int before = node.Connection.SentOf(MsgType.TeamRequestResult).Count;
        await Rig.SendAsync(node, MsgType.TeamCreateRequest, TeamFrames.Create(requestKey == 0 ? NextKey() : requestKey, name));
        await SettleAsync();
        return LastResult(node, before);
    }

    private static TeamRequestResultT LastResult(JoinedNode node, int before)
    {
        var results = node.Connection.SentOf(MsgType.TeamRequestResult);
        Assert.True(results.Count > before, "the server sent no TeamRequestResult");
        return results[^1].Decode<TeamRequestResult>().UnPack();
    }

    private ulong _key = 1000;

    private ulong NextKey() => ++_key;

    /// <summary>The proof a client sends for a team password (protocol.md 4.3).</summary>
    public static byte[] Proof(JoinedNode node, string password) =>
        GatewayState.ComputeProof(TeamRules.HashPassword(password), node.Node.Nonce, node.Node.Hello.PlayerKey.ToArray());

    public async ValueTask DisposeAsync() => await Rig.DisposeAsync();
}

/// <summary>Builders for the frames a node sends while it picks a team.</summary>
public static class TeamFrames
{
    public static FlatBufferBuilder Choice(ulong requestKey, int teamId, byte[]? password = null)
    {
        var fbb = new FlatBufferBuilder(128);
        var t = new TeamChoiceT
        {
            RequestKey = new Id128T { Lo = requestKey, Hi = 0 },
            TeamId = (ushort)teamId,
            Password = password is null ? [] : [.. password],
        };
        fbb.Finish(TeamChoice.Pack(fbb, t).Value);
        return fbb;
    }

    public static FlatBufferBuilder Create(ulong requestKey, string name, uint colorRgb = 0)
    {
        var fbb = new FlatBufferBuilder(128);
        var t = new TeamCreateRequestT { RequestKey = new Id128T { Lo = requestKey, Hi = 0 }, Name = name, ColorRgb = colorRgb };
        fbb.Finish(TeamCreateRequest.Pack(fbb, t).Value);
        return fbb;
    }

    /// <summary>An empty table: enough for a frame the team module swallows without decoding it.</summary>
    public static FlatBufferBuilder Empty()
    {
        var fbb = new FlatBufferBuilder(16);
        fbb.StartTable(0);
        fbb.Finish(fbb.EndTable());
        return fbb;
    }
}

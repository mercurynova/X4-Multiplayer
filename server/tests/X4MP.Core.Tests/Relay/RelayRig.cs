using Google.FlatBuffers;
using Microsoft.Extensions.Time.Testing;
using X4MP.Core.Relay;
using X4MP.Core.Session;
using X4MP.Core.Teams;
using X4MP.Core.Tests.Session;
using X4MP.Core.Tests.Teams;
using X4MP.Core.World;
using X4MP.Proto;

namespace X4MP.Core.Tests.Relay;

/// <summary>An <see cref="IChatStore"/> that remembers everything.</summary>
public sealed class MemoryChatStore : IChatStore
{
    private readonly object _gate = new();

    public List<ChatLine> Lines { get; } = [];

    public Dictionary<int, MuteEntry?> Mutes { get; } = [];

    public List<MuteEntry> Preloaded { get; } = [];

    public bool Append(ChatLine line)
    {
        lock (_gate)
        {
            Lines.Add(line);
        }

        return true;
    }

    public bool SetMute(int playerId, MuteEntry? entry)
    {
        lock (_gate)
        {
            Mutes[playerId] = entry;
        }

        return true;
    }

    public IReadOnlyList<MuteEntry> LoadMutes() => Preloaded;
}

/// <summary>An interest answer the test controls.</summary>
public sealed class FakeInterest : IRelayInterest
{
    public HashSet<(int Player, ushort Sector)> Sectors { get; } = [];

    public HashSet<(int Player, uint NetId)> Held { get; } = [];

    public bool IsInterested(int playerId, ushort sector) => Sectors.Contains((playerId, sector));

    public bool IsHeld(int playerId, uint netId) => Held.Contains((playerId, netId));
}

/// <summary>The actor with a world mirror and a <see cref="RelayModule"/> behind it (and optionally the Teams module in front).</summary>
public sealed class RelayRig : IAsyncDisposable
{
    public RelayRig(RelayOptions? options = null, bool teams = false, FakeInterest? interest = null, MemoryChatStore? chat = null, TeamOptions? teamOptions = null)
    {
        Options = options ?? new RelayOptions();
        Time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
        Chat = chat ?? new MemoryChatStore();
        Mirror = new WorldMirror(Time);
        Interest = interest;
        Events = new RecordingEvents();
        Relay = new RelayModule(() => Options, Time, Events, Chat, Mirror, Interest);
        var modules = new List<ISessionModule>();
        if (teams)
        {
            Teams = new TeamModule(() => teamOptions ?? new TeamOptions(), new MemoryTeamStore(), Time);
            modules.Add(Teams);
        }

        modules.Add(Mirror);
        modules.Add(Relay);
        Rig = new ActorRig(Time, null, null, [.. modules]);
    }

    public RelayOptions Options { get; }

    public FakeTimeProvider Time { get; }

    public MemoryChatStore Chat { get; }

    /// <summary>What the relay published (the actor's own events are in <c>Rig.Events</c>).</summary>
    public RecordingEvents Events { get; }

    public WorldMirror Mirror { get; }

    public FakeInterest? Interest { get; }

    public RelayModule Relay { get; }

    public TeamModule? Teams { get; }

    public ActorRig Rig { get; }

    public SessionActor Actor => Rig.Actor;

    /// <summary>Joins a node and walks it to in-game (the Teams module, when present, needs two flushes to place it first).</summary>
    public async Task<JoinedNode> JoinInGameAsync(string name, Role roles = Role.Client)
    {
        var node = await Rig.JoinAsync(name, roles);
        await Rig.Actor.FlushAsync();
        await Rig.Actor.FlushAsync();
        await Rig.BringInGameAsync(node);
        return node;
    }

    public Task<JoinedNode> JoinAuthorityAsync(string name = "Boss") => JoinInGameAsync(name, Role.Authority | Role.Client);

    /// <summary>The node sends a frame and the actor has handled it.</summary>
    public Task SendAsync(JoinedNode node, MsgType type, FlatBufferBuilder frame) => Rig.SendAsync(node, type, frame);

    /// <summary>Same for a payload built elsewhere (the authority's <c>EntitySpawn</c>s).</summary>
    public async Task SendPayloadAsync(JoinedNode node, MsgType type, byte[] payload)
    {
        long before = Actor.FramesReceived;
        node.Connection.PushPayload(type, payload);
        long until = Environment.TickCount64 + 5000;
        while (Actor.FramesReceived == before)
        {
            Assert.True(Environment.TickCount64 < until, $"the actor never saw the {type} frame");
            await Task.Delay(1);
        }

        await Actor.FlushAsync();
    }

    public Task ChatAsync(JoinedNode node, ChatChannel channel, string text, int to = 0) =>
        SendAsync(node, MsgType.ChatSend, RelayFrames.Chat(channel, text, to));

    public async Task AdvanceAsync(double seconds)
    {
        Time.Advance(TimeSpan.FromSeconds(seconds));
        await Rig.Actor.FlushAsync();
        await Rig.Actor.FlushAsync();
    }

    public async ValueTask DisposeAsync() => await Rig.DisposeAsync();

    /// <summary>Texts of the chat messages a node received, in order.</summary>
    public static List<ChatMessageT> Chats(JoinedNode node) =>
        [.. node.Connection.SentOf(MsgType.ChatMessage).Select(f => f.Decode<ChatMessage>().UnPack())];

    public static List<ChatMessageT> PlayerChats(JoinedNode node) =>
        [.. Chats(node).Where(c => c.Channel != ChatChannel.System)];
}

/// <summary>Builders for the frames the relay handles.</summary>
public static class RelayFrames
{
    public static FlatBufferBuilder Chat(ChatChannel channel, string text, int to = 0) =>
        Pack(fbb => ChatSend.Pack(fbb, new ChatSendT { Channel = channel, ToPlayer = (ushort)to, Text = text }).Value, 128);

    public static FlatBufferBuilder State(
        uint seq, ulong timeUs, int px, ushort sector = 1, uint netId = 0, int py = 0, int pz = 0, ushort flags = 0) =>
        Pack(
            fbb => PlayerState.Pack(fbb, new PlayerStateT
            {
                Seq = seq, SampleTimeUs = timeUs, NetId = netId, Sector = sector, Flags = flags, Px = px, Py = py, Pz = pz, Hull = 255, Shield = 255,
            }).Value,
            96);

    public static FlatBufferBuilder KillClaim(ulong key, uint target, uint requestId = 1) =>
        Intent(key, requestId, IntentBodyUnion.FromKillClaim(new KillClaimT { Target = target, Killer = 9001 }));

    public static FlatBufferBuilder Intent(ulong key, uint requestId, IntentBodyUnion body) =>
        Pack(fbb => X4MP.Proto.Intent.Pack(fbb, new IntentT { RequestKey = new Id128T { Lo = key, Hi = 7 }, RequestId = requestId, Body = body }).Value, 128);

    public static FlatBufferBuilder IntentResult(ushort player, ulong key, uint requestId, IntentStatus status = IntentStatus.Accepted) =>
        Pack(
            fbb => X4MP.Proto.IntentResult.Pack(fbb, new IntentResultT
            {
                RequestKey = new Id128T { Lo = key, Hi = 7 }, RequestId = requestId, PlayerId = player, Status = status,
            }).Value,
            96);

    /// <param name="claimedPlayerId">What the (lying) client puts in <c>player_id</c>; the server must overwrite it.</param>
    public static FlatBufferBuilder PlayerShip(ulong key, string macro = "ship_arg_s_fighter_01_a_macro", ushort claimedPlayerId = 0, ulong keyHi = 0) =>
        Pack(fbb => X4MP.Proto.PlayerShip.Pack(fbb, new PlayerShipT { RequestKey = new Id128T { Lo = key, Hi = keyHi }, ShipMacro = macro, Name = "Pilot", Idcode = "ABC-123", Sector = 1, PlayerId = claimedPlayerId }).Value, 128);

    public static FlatBufferBuilder GameEvent(ushort sector, GameEventBodyUnion body) =>
        Pack(fbb => X4MP.Proto.GameEvent.Pack(fbb, new GameEventT { Sector = sector, GameTime = 12.5, Body = body }).Value, 128);

    public static GameEventBodyUnion Kill(ushort killerPlayer = 0) =>
        GameEventBodyUnion.FromKillEvent(new KillEventT { Victim = 4711, Killer = 9001, KillerPlayer = killerPlayer });

    public static GameEventBodyUnion PlayerDied(ushort player) =>
        GameEventBodyUnion.FromPlayerDiedEvent(new PlayerDiedEventT { PlayerId = player, Killer = 5 });

    private static FlatBufferBuilder Pack(Func<FlatBufferBuilder, int> pack, int size)
    {
        var fbb = new FlatBufferBuilder(size);
        fbb.Finish(pack(fbb));
        return fbb;
    }
}

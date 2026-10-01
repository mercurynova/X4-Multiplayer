using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Google.FlatBuffers;
using X4MP.Core.Events;
using X4MP.Core.Metrics;
using X4MP.Core.Net;
using X4MP.Core.Relay;
using X4MP.Core.Session;
using X4MP.Core.Teams;
using X4MP.Core.World;
using X4MP.Proto;
using X4MP.Protocol;
using X4MP.Protocol.Client;
using Xunit.Abstractions;

namespace X4MP.Server.Tests.Net;

/// <summary>A scripted node on a real <see cref="TcpNodeClient"/>: a background pump keeps every frame the server sends.</summary>
public sealed class Peer : IAsyncDisposable
{
    private readonly ConcurrentQueue<Frame> _frames = new();
    private readonly CancellationTokenSource _stop = new();
    private Task _pump = Task.CompletedTask;

    private Peer(TcpNodeClient client) => Client = client;

    public TcpNodeClient Client { get; }

    public int PlayerId => Client.Welcome.PlayerId;

    public static async Task<Peer> JoinAsync(NetHarness net, string name, Role roles = Role.Client)
    {
        var handle = await net.ConnectAsync();
        return await JoinAsync(handle.Stream, name, roles);
    }

    public static async Task<Peer> JoinAsync(Stream stream, string name, Role roles = Role.Client)
    {
        var client = await TcpNodeClient.ConnectAsync(
            stream, new NodeClientOptions { PlayerName = name, PlayerKey = RandomNumberGenerator.GetBytes(32), RequestedRoles = roles });
        var peer = new Peer(client);
        peer._pump = Task.Run(() => peer.PumpAsync(peer._stop.Token));
        return peer;
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        try
        {
            while (await Client.ReceiveAsync(ct).ConfigureAwait(false) is { } frame)
            {
                _frames.Enqueue(frame);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // closed
        }
    }

    public Task SendAsync<T>(MsgType type, Func<FlatBufferBuilder, Offset<T>> pack) where T : struct => Client.SendAsync(type, pack);

    /// <summary>
    /// Walks the node to in-game. The server's frame reader checks every frame against the node's current phase, so each report waits
    /// until the server has moved the node (a real mod answers server messages instead of firing everything at once).
    /// </summary>
    public async Task BringInGameAsync(Func<Func<SessionSnapshot, bool>, Task> waitFor)
    {
        foreach (var phase in new[] { NodePhase.SyncingSave, NodePhase.Verifying, NodePhase.Loading, NodePhase.Matching, NodePhase.CatchingUp })
        {
            await Client.SendAsync(MsgType.LoadStatus, b => LoadStatus.Pack(b, new LoadStatusT { Phase = phase, Detail = "", Error = DisconnectCode.None }));
            await waitFor(s => s.Nodes.Any(n => n.PlayerId == PlayerId && n.Phase == phase));
        }

        await Client.SendAsync(MsgType.NodeReady, b => NodeReady.CreateNodeReady(b, 42));
        await waitFor(s => s.Nodes.Any(n => n.PlayerId == PlayerId && n.Phase == NodePhase.InGame));
    }

    public List<T> All<T>(MsgType type, Func<Frame, T> decode) => [.. _frames.Where(f => f.Type == type).Select(decode)];

    public List<ChatMessageT> Chats() => All(MsgType.ChatMessage, f => MessageRegistry.Default.Decode<ChatMessage>(f).UnPack());

    public List<ChatMessageT> PlayerChats() => [.. Chats().Where(c => c.Channel != ChatChannel.System)];

    public List<IntentResultT> Results() => All(MsgType.IntentResult, f => MessageRegistry.Default.Decode<IntentResult>(f).UnPack());

    public List<IntentT> Intents() => All(MsgType.Intent, f => MessageRegistry.Default.Decode<Intent>(f).UnPack());

    public List<GameEventT> GameEvents() => All(MsgType.GameEvent, f => MessageRegistry.Default.Decode<GameEvent>(f).UnPack());

    public async Task<T> WaitForAsync<T>(Func<Peer, List<T>> read, Func<T, bool> match, string what, int timeoutMs = 5000)
    {
        long until = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            var found = read(this).FirstOrDefault(match);
            if (found is not null)
            {
                return found;
            }

            Assert.True(Environment.TickCount64 < until, "never received: " + what);
            await Task.Delay(5);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await Client.DisposeAsync();
        try
        {
            await _pump;
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // closed
        }

        _stop.Dispose();
    }
}

/// <summary>An interest answer the test controls.</summary>
public sealed class SectorInterest : IRelayInterest
{
    public ConcurrentDictionary<(int Player, ushort Sector), bool> Sectors { get; } = new();

    public bool IsInterested(int playerId, ushort sector) => Sectors.ContainsKey((playerId, sector));

    public bool IsHeld(int playerId, uint netId) => true;
}

/// <summary>
/// M1-10 acceptance with the real gateway, the real <see cref="SessionActor"/>, the relay and real <see cref="TcpNodeClient"/>s over
/// the in-process transport and over TCP: chat and mute, kill claims, authority events, intent timeout, team chat, inbound floods.
/// </summary>
[Collection("net")]
public class RelayIntegrationTests(ITestOutputHelper output)
{
    public static TheoryData<string> Kinds => ["inproc", "tcp"];

    private static NetOptions Fast() => new() { AuthFailureDelayMs = 0, HandshakeTimeoutSeconds = 2, MaxConnectionsPerIp = 50, MaxPlayers = 16 };

    private sealed class Setup : IAsyncDisposable
    {
        private readonly List<Peer> _peers = [];

        public required ActorFixture Fixture { get; init; }

        public required NetHarness Net { get; init; }

        public required RelayModule Relay { get; init; }

        public TeamModule? Teams { get; init; }

        public SessionActor Actor => Fixture.Actor;

        public static async Task<Setup> CreateAsync(
            string kind, RelayOptions? options = null, IRelayInterest? interest = null, TeamOptions? teams = null, NetOptions? net = null)
        {
            var fixture = new ActorFixture(net ?? Fast());
            TeamModule? teamModule = null;
            if (teams is not null)
            {
                teamModule = new TeamModule(teams);
                fixture.Modules.Add(teamModule);
            }

            var mirror = new WorldMirror();
            var relayOptions = options ?? new RelayOptions();
            var relay = new RelayModule(() => relayOptions, TimeProvider.System, fixture.Events, null, mirror, interest);
            fixture.Modules.Add(mirror);
            fixture.Modules.Add(relay);
            var harness = await NetHarness.CreateAsync(kind, fixture.Net, withGateway: true, handler: fixture.Handler);
            return new Setup { Fixture = fixture, Net = harness, Relay = relay, Teams = teamModule };
        }

        public async Task<Peer> JoinAsync(string name, Role roles = Role.Client, bool inGame = true)
        {
            var peer = await Peer.JoinAsync(Net, name, roles);
            _peers.Add(peer);
            if (inGame)
            {
                await peer.BringInGameAsync(async condition => await Fixture.WaitForAsync(condition));
            }

            return peer;
        }

        public Task<Peer> JoinAuthorityAsync() => JoinAsync("Boss", Role.Authority | Role.Client);

        /// <summary>Waits until every node is in game, so the relay will route to it.</summary>
        public Task<SessionSnapshot> WaitInGameAsync(int count) =>
            Fixture.WaitForAsync(s => s.Nodes.Count(n => n.Phase == NodePhase.InGame) == count);

        public async ValueTask DisposeAsync()
        {
            foreach (var peer in _peers)
            {
                await peer.DisposeAsync();
            }

            await Net.DisposeAsync();
            await Fixture.DisposeAsync();
        }
    }

    private static Func<FlatBufferBuilder, Offset<ChatSend>> Chat(ChatChannel channel, string text, int to = 0) =>
        b => ChatSend.Pack(b, new ChatSendT { Channel = channel, ToPlayer = (ushort)to, Text = text });

    private static Func<FlatBufferBuilder, Offset<Intent>> KillClaim(ulong key, uint target) =>
        b => Intent.Pack(b, new IntentT
        {
            RequestKey = new Id128T { Lo = key, Hi = 1 },
            RequestId = (uint)key,
            Body = IntentBodyUnion.FromKillClaim(new KillClaimT { Target = target, Killer = 9001 }),
        });

    private static async Task SettleAsync(Setup s)
    {
        await s.Actor.FlushAsync();
        await s.Actor.FlushAsync();
        await Task.Delay(60); // frames in flight to the peers
    }

    // ------------------------------------------------------------------ chat and mute

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task ChatFromAReachesBAndCButNotAfterAIsMuted(string kind)
    {
        await using var s = await Setup.CreateAsync(kind);
        var a = await s.JoinAsync("Alice");
        var b = await s.JoinAsync("Bob");
        var c = await s.JoinAsync("Cleo");
        await s.WaitInGameAsync(3);

        await a.SendAsync(MsgType.ChatSend, Chat(ChatChannel.All, "hello"));
        await b.WaitForAsync(p => p.PlayerChats(), m => m.Text == "hello", "chat to Bob");
        await c.WaitForAsync(p => p.PlayerChats(), m => m.Text == "hello", "chat to Cleo");
        Assert.Equal("Alice", b.PlayerChats()[0].FromName);

        Assert.True(await s.Relay.MuteAsync(a.PlayerId, actor: "admin"));
        await a.SendAsync(MsgType.ChatSend, Chat(ChatChannel.All, "muted line"));
        await Task.Delay(50);
        await b.SendAsync(MsgType.ChatSend, Chat(ChatChannel.All, "sentinel")); // arrives after the muted line, so the line would have been delivered by now
        await c.WaitForAsync(p => p.PlayerChats(), m => m.Text == "sentinel", "sentinel");
        await SettleAsync(s);

        Assert.DoesNotContain(b.PlayerChats(), m => m.Text == "muted line");
        Assert.DoesNotContain(c.PlayerChats(), m => m.Text == "muted line");
        await a.WaitForAsync(p => p.Chats(), m => m.Channel == ChatChannel.System && m.Text.Contains("muted", StringComparison.OrdinalIgnoreCase), "the mute notice");
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task TeamChatStaysWithinTheTeam(string kind)
    {
        await using var s = await Setup.CreateAsync(kind, teams: new TeamOptions { AutoAssign = AutoAssignStrategy.Balance });
        Assert.True((await s.Teams!.ApplyPresetAsync(TeamPreset.TwoTeams)).Ok);
        var a = await s.JoinAsync("Alice"); // team 1
        var b = await s.JoinAsync("Bob"); // team 2
        var c = await s.JoinAsync("Cleo"); // team 1
        await s.WaitInGameAsync(3);
        Assert.Equal(s.Teams.TeamOf(a.PlayerId), s.Teams.TeamOf(c.PlayerId));
        Assert.NotEqual(s.Teams.TeamOf(a.PlayerId), s.Teams.TeamOf(b.PlayerId));

        await a.SendAsync(MsgType.ChatSend, Chat(ChatChannel.Team, "flank left"));
        await c.WaitForAsync(p => p.PlayerChats(), m => m.Text == "flank left" && m.Channel == ChatChannel.Team, "team chat to Cleo");
        await b.SendAsync(MsgType.ChatSend, Chat(ChatChannel.All, "sentinel"));
        await a.WaitForAsync(p => p.PlayerChats(), m => m.Text == "sentinel", "sentinel");
        await SettleAsync(s);

        Assert.DoesNotContain(b.PlayerChats(), m => m.Text == "flank left");
    }

    // ------------------------------------------------------------------ intents

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task AKillClaimReachesOnlyTheAuthorityAndItsAnswerReturns(string kind)
    {
        await using var s = await Setup.CreateAsync(kind);
        var authority = await s.JoinAuthorityAsync();
        var a = await s.JoinAsync("Alice");
        var b = await s.JoinAsync("Bob");
        await s.WaitInGameAsync(3);

        await a.SendAsync(MsgType.Intent, KillClaim(key: 77, target: 4711));

        var forwarded = await authority.WaitForAsync(p => p.Intents(), i => i.RequestKey.Lo == 77, "the kill claim at the authority");
        Assert.Equal(a.PlayerId, forwarded.PlayerId);
        Assert.Equal(IntentBody.KillClaim, forwarded.Body.Type);
        await SettleAsync(s);
        Assert.Empty(b.Intents());
        Assert.Empty(a.Intents());

        await authority.SendAsync(MsgType.IntentResult, bld => IntentResult.Pack(bld, new IntentResultT
        {
            RequestKey = new Id128T { Lo = 77, Hi = 1 }, RequestId = 77, PlayerId = (ushort)a.PlayerId, Status = IntentStatus.Accepted,
        }));
        var result = await a.WaitForAsync(p => p.Results(), r => r.RequestKey.Lo == 77, "the intent result");
        Assert.Equal(IntentStatus.Accepted, result.Status);
        Assert.Empty(b.Results());
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task ASilentAuthorityGivesRejectedTimeoutExactlyOnce(string kind)
    {
        await using var s = await Setup.CreateAsync(kind, new RelayOptions { IntentTimeoutMs = 400 });
        var authority = await s.JoinAuthorityAsync();
        var a = await s.JoinAsync("Alice");
        await s.WaitInGameAsync(2);

        await a.SendAsync(MsgType.Intent, KillClaim(key: 5, target: 1));

        var timeout = await a.WaitForAsync(p => p.Results(), r => r.RequestKey.Lo == 5, "the timeout", timeoutMs: 5000);
        Assert.Equal(IntentStatus.Rejected, timeout.Status);
        Assert.Equal(RejectReason.Timeout, timeout.Reason);

        // a late answer from the authority is not a second result
        await authority.SendAsync(MsgType.IntentResult, bld => IntentResult.Pack(bld, new IntentResultT
        {
            RequestKey = new Id128T { Lo = 5, Hi = 1 }, RequestId = 5, PlayerId = (ushort)a.PlayerId, Status = IntentStatus.Accepted,
        }));
        await Task.Delay(800);
        Assert.Single(a.Results());
    }

    // ------------------------------------------------------------------ game events

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task AnAuthorityGameEventReachesInterestedClientsAndTheEventLog(string kind)
    {
        var interest = new SectorInterest();
        await using var s = await Setup.CreateAsync(kind, interest: interest);
        var authority = await s.JoinAuthorityAsync();
        var a = await s.JoinAsync("Alice");
        var b = await s.JoinAsync("Bob");
        await s.WaitInGameAsync(3);
        interest.Sectors[(a.PlayerId, 5)] = true;

        await authority.SendAsync(MsgType.GameEvent, bld => GameEvent.Pack(bld, new GameEventT
        {
            Sector = 5,
            GameTime = 3.5,
            Body = GameEventBodyUnion.FromKillEvent(new KillEventT { Victim = 4711, Killer = 9001, KillerPlayer = (ushort)a.PlayerId }),
        }));

        var seen = await a.WaitForAsync(p => p.GameEvents(), e => e.Body.Type == GameEventBody.KillEvent, "the kill event");
        Assert.Equal(1ul, seen.EventSeq);
        await SettleAsync(s);
        Assert.Empty(b.GameEvents());
        var logged = Assert.Single(s.Fixture.Events.OfType<GameEventOccurred>());
        Assert.Equal("Kill", logged.Kind);
        Assert.Equal(5, logged.SectorId);
    }

    // ------------------------------------------------------------------ inbound floods

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task AFloodOfPlayerStatesAtTenThousandPerSecondKeepsMemoryFlatAndOthersUnaffected(string kind)
    {
        const int Rate = 10_000;
        const int Seconds = 3;
        const int Batch = 100; // frames per write, one write every 10 ms
        await using var s = await Setup.CreateAsync(kind, new RelayOptions { ChatBurst = 100, ChatPerSecond = 100 });
        var authority = await s.JoinAuthorityAsync();
        var flooder = await s.JoinAsync("Flooder");
        var talker = await s.JoinAsync("Talker");
        var listener = await s.JoinAsync("Listener");
        await s.WaitInGameAsync(4);

        // every batch is encoded up front so the flooder spends its time on the wire
        var batches = new List<byte[]>();
        uint seq = 0;
        for (int i = 0; i < Rate * Seconds / Batch; i++)
        {
            var bytes = new List<byte>(Batch * 100);
            for (int j = 0; j < Batch; j++)
            {
                seq++;
                bytes.AddRange(MessageEncoder.EncodeFrame(MsgType.PlayerState, b => PlayerState.Pack(b, new PlayerStateT
                {
                    Seq = seq, SampleTimeUs = seq * 100UL, Sector = 1, Px = (int)(seq % 100_000), Hull = 255, Shield = 255,
                })));
            }

            batches.Add([.. bytes]);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long memoryBefore = GC.GetTotalMemory(true);
        long coalescedBefore = ServerMetrics.InboundCoalesced;
        long handledBefore = s.Relay.Stats.StatesReceived;

        int maxPending = 0;
        using var stop = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                maxPending = Math.Max(maxPending, s.Actor.PendingInputs);
                await Task.Delay(5);
            }
        });

        // a bystander keeps chatting while the flood runs; the listener counts what arrives
        var chatter = Task.Run(async () =>
        {
            for (int i = 0; i < Seconds * 8; i++)
            {
                long stamp = Stopwatch.GetTimestamp();
                await talker.SendAsync(MsgType.ChatSend, Chat(ChatChannel.All, "t" + stamp.ToString(CultureInfo.InvariantCulture)));
                await Task.Delay(125);
            }
        });

        var clock = Stopwatch.StartNew();
        for (int i = 0; i < batches.Count; i++)
        {
            await flooder.Client.SendRawFrameAsync(batches[i]);
            long due = (i + 1) * 10;
            while (clock.ElapsedMilliseconds < due)
            {
                await Task.Delay(1);
            }
        }

        double sendSeconds = clock.Elapsed.TotalSeconds;
        await chatter;
        await Task.Delay(300);
        await stop.CancelAsync();
        await sampler;
        await s.Actor.FlushAsync();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long memoryAfter = GC.GetTotalMemory(true);
        long coalesced = ServerMetrics.InboundCoalesced - coalescedBefore;
        long handled = s.Relay.Stats.StatesReceived - handledBefore;
        int talked = listener.PlayerChats().Count(c => c.Text.StartsWith('t'));

        output.WriteLine(
            $"[{kind}] sent {seq} PlayerStates in {sendSeconds:F2}s ({seq / sendSeconds:F0}/s); the actor handled {handled}, coalesced {coalesced}; " +
            $"max pending inputs {maxPending}; heap {memoryBefore / 1024} KiB -> {memoryAfter / 1024} KiB (delta {(memoryAfter - memoryBefore) / 1024} KiB); " +
            $"listener got {talked}/{Seconds * 8} chat lines during the flood");

        Assert.True(seq / sendSeconds > 5000, $"the flood only reached {seq / sendSeconds:F0} msgs/s");
        Assert.True(maxPending <= 64, $"max pending inputs {maxPending}"); // the mailbox never grew with the flood
        Assert.True(coalesced + handled >= seq * 0.9, $"coalesced {coalesced} + handled {handled} of {seq}");
        Assert.True(handled < seq, "every state reached the actor: nothing was coalesced");
        Assert.True(memoryAfter - memoryBefore < 8 * 1024 * 1024, $"heap grew by {(memoryAfter - memoryBefore) / 1024} KiB");
        Assert.Equal(Seconds * 8, talked); // the bystander's chat all arrived
        var snapshot = await s.Actor.GetSnapshotAsync();
        Assert.Equal(4, snapshot.Nodes.Count(n => n.Connected)); // nobody was disconnected, the flooder included
        _ = authority;
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task AFloodOfChatIsRateLimitedAndTheSustainedFlooderIsClosedWhileOthersCarryOn(string kind)
    {
        await using var s = await Setup.CreateAsync(kind, new RelayOptions { RateLimitHitsPerMinute = 100 });
        var flooder = await s.JoinAsync("Flooder");
        var talker = await s.JoinAsync("Talker");
        var listener = await s.JoinAsync("Listener");
        await s.WaitInGameAsync(3);

        var batch = new List<byte>();
        for (int i = 0; i < 100; i++)
        {
            batch.AddRange(MessageEncoder.EncodeFrame(MsgType.ChatSend, b => ChatSend.Pack(b, new ChatSendT { Channel = ChatChannel.All, Text = "spam" })));
        }

        byte[] bytes = [.. batch];
        long droppedBefore = ServerMetrics.InboundDropped;
        var sw = Stopwatch.StartNew();
        try
        {
            for (int i = 0; i < 200 && sw.Elapsed < TimeSpan.FromSeconds(10); i++)
            {
                await flooder.Client.SendRawFrameAsync(bytes); // 20,000 chat lines
                await Task.Delay(1);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // the server closed us: expected
        }

        await talker.SendAsync(MsgType.ChatSend, Chat(ChatChannel.All, "still here"));
        await listener.WaitForAsync(p => p.PlayerChats(), m => m.Text == "still here", "the bystander's chat");
        var snapshot = await s.Fixture.WaitForAsync(sn => sn.Nodes.Count(n => n.Connected) == 2);

        Assert.DoesNotContain(snapshot.Nodes, n => n.Name == "Flooder" && n.Connected);
        Assert.True(listener.PlayerChats().Count(m => m.Text == "spam") <= 10, "the burst allowance let the spam through");
        output.WriteLine($"[{kind}] chat flood: {s.Relay.Stats.ChatRateLimited} rate-limited, {ServerMetrics.InboundDropped - droppedBefore} dropped at the inbound queue; flooder closed");
    }
}

using Google.FlatBuffers;
using Microsoft.Extensions.Time.Testing;
using X4MP.Core.Admin;
using X4MP.Core.Session;
using X4MP.Core.Tests.Session;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.Core.Tests.Admin;

/// <summary>
/// The in-game admin bridge: an <see cref="AdminCommand"/> from a node with the Admin role becomes a kick or a ban and is always
/// answered with an <see cref="AdminResult"/> carrying the same <c>request_id</c>. The tests drive the real actor through
/// <see cref="ActorRig"/>, so the reply really travels the Control lane and a kick really closes the node.
/// </summary>
public sealed class AdminModuleTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task KickClosesTheTargetAndAnswersTheAdmin()
    {
        await using var h = await HarnessAsync();

        await h.Rig.SendAsync(h.Boss, MsgType.AdminCommand, Kick(h.Target.PlayerId, requestId: 7, "spamming the gate"));
        await WaitForAsync(() => h.Boss.Connection.SentOf(MsgType.AdminResult).Count == 1);

        AdminResultT result = h.Boss.Connection.SentOf(MsgType.AdminResult)[0].Decode<AdminResult>().UnPack();
        Assert.Equal(7u, result.RequestId);
        Assert.True(result.Ok);
        Assert.Contains("Pia", result.Message);
        Assert.Equal(DisconnectCode.Kicked, h.Target.Connection.CloseCode);
        Assert.Equal("spamming the gate", h.Target.Connection.CloseDetail);
        Assert.Empty(h.Target.Connection.SentOf(MsgType.AdminResult));  // only the requester is answered
    }

    [Fact]
    public async Task KickOfAPlayerThatIsNotInTheSessionIsRefused()
    {
        await using var h = await HarnessAsync();

        await h.Rig.SendAsync(h.Boss, MsgType.AdminCommand, Kick(playerId: 999, requestId: 8, "nobody"));
        await WaitForAsync(() => h.Boss.Connection.SentOf(MsgType.AdminResult).Count == 1);

        AdminResultT result = h.Boss.Connection.SentOf(MsgType.AdminResult)[0].Decode<AdminResult>().UnPack();
        Assert.Equal(8u, result.RequestId);
        Assert.False(result.Ok);
        Assert.Contains("999", result.Message);
    }

    [Fact]
    public async Task TheSameRequestIdIsAnsweredOnce()
    {
        await using var h = await HarnessAsync();

        await h.Rig.SendAsync(h.Boss, MsgType.AdminCommand, Kick(h.Target.PlayerId, requestId: 9, "twice"));
        await WaitForAsync(() => h.Boss.Connection.SentOf(MsgType.AdminResult).Count == 1);
        await h.Rig.SendAsync(h.Boss, MsgType.AdminCommand, Kick(h.Target.PlayerId, requestId: 9, "twice"));
        await Task.Delay(50);

        Assert.Single(h.Boss.Connection.SentOf(MsgType.AdminResult));
    }

    [Fact]
    public async Task BanWithoutADurationIsPermanentAndKicksWithTheBannedCode()
    {
        await using var h = await HarnessAsync();
        await BindPlayerAsync(h);

        await h.Rig.SendAsync(h.Boss, MsgType.AdminCommand, Ban(h.Target.PlayerId, requestId: 10, durationS: 0, "griefing"));
        await WaitForAsync(() => h.Boss.Connection.SentOf(MsgType.AdminResult).Count == 1);

        AdminResultT result = h.Boss.Connection.SentOf(MsgType.AdminResult)[0].Decode<AdminResult>().UnPack();
        Assert.True(result.Ok);
        Assert.Equal(DisconnectCode.Banned, h.Target.Connection.CloseCode);

        BanInfo? ban = await h.Bans.FindActiveBanAsync(h.Target.Node.KeyHash, null, h.Rig.Time.GetUtcNow(), CancellationToken.None);
        Assert.NotNull(ban);
        Assert.Equal("griefing", ban!.Reason);
        Assert.False(ban.IsIpBan);

        await h.Rig.AdvanceAsync(TimeSpan.FromDays(400));
        Assert.NotNull(await h.Bans.FindActiveBanAsync(h.Target.Node.KeyHash, null, h.Rig.Time.GetUtcNow(), CancellationToken.None));
    }

    [Fact]
    public async Task BanWithADurationExpires()
    {
        await using var h = await HarnessAsync();
        await BindPlayerAsync(h);

        await h.Rig.SendAsync(h.Boss, MsgType.AdminCommand, Ban(h.Target.PlayerId, requestId: 11, durationS: 3600, "one hour"));
        await WaitForAsync(() => h.Boss.Connection.SentOf(MsgType.AdminResult).Count == 1);

        Assert.NotNull(await h.Bans.FindActiveBanAsync(h.Target.Node.KeyHash, null, h.Rig.Time.GetUtcNow(), CancellationToken.None));
        await h.Rig.AdvanceAsync(TimeSpan.FromMinutes(59));
        Assert.NotNull(await h.Bans.FindActiveBanAsync(h.Target.Node.KeyHash, null, h.Rig.Time.GetUtcNow(), CancellationToken.None));
        await h.Rig.AdvanceAsync(TimeSpan.FromMinutes(2));
        Assert.Null(await h.Bans.FindActiveBanAsync(h.Target.Node.KeyHash, null, h.Rig.Time.GetUtcNow(), CancellationToken.None));
    }

    [Fact]
    public async Task BanWithoutAStoredPlayerRowIsRefusedAndDoesNotKick()
    {
        await using var h = await HarnessAsync();  // deliberately without BindPlayerAsync

        await h.Rig.SendAsync(h.Boss, MsgType.AdminCommand, Ban(h.Target.PlayerId, requestId: 12, durationS: 0, "no row"));
        await WaitForAsync(() => h.Boss.Connection.SentOf(MsgType.AdminResult).Count == 1);

        AdminResultT result = h.Boss.Connection.SentOf(MsgType.AdminResult)[0].Decode<AdminResult>().UnPack();
        Assert.False(result.Ok);
        Assert.Contains("could not be banned", result.Message);
        Assert.Null(h.Target.Connection.CloseCode);
    }

    [Fact]
    public async Task EveryOtherCommandArmIsRefusedWithAPointerToTheAdminApi()
    {
        await using var h = await HarnessAsync();

        var fbb = new FlatBufferBuilder(128);
        var command = new AdminCommandT
        {
            RequestKey = new Id128T { Lo = 13, Hi = 0 },
            RequestId = 13,
            Body = AdminBodyUnion.FromBroadcastCmd(new BroadcastCmdT { Text = "hello", Severity = 1 }),
        };
        fbb.Finish(AdminCommand.Pack(fbb, command).Value);
        await h.Rig.SendAsync(h.Boss, MsgType.AdminCommand, fbb);
        await WaitForAsync(() => h.Boss.Connection.SentOf(MsgType.AdminResult).Count == 1);

        AdminResultT result = h.Boss.Connection.SentOf(MsgType.AdminResult)[0].Decode<AdminResult>().UnPack();
        Assert.False(result.Ok);
        Assert.Contains("kick and ban", result.Message);
    }

    private sealed class Harness : IAsyncDisposable
    {
        public required ActorRig Rig { get; init; }

        public required InMemoryNodeStore Bans { get; init; }

        public required JoinedNode Boss { get; init; }

        public required JoinedNode Target { get; init; }

        public ValueTask DisposeAsync() => Rig.DisposeAsync();
    }

    private static async Task<Harness> HarnessAsync()
    {
        var time = new FakeTimeProvider(Start);
        var bans = new InMemoryNodeStore();
        var admin = new AdminModule(bans, time);
        var rig = new ActorRig(time, null, null, [admin]);
        JoinedNode boss = await rig.JoinAsync("Boss", Role.Admin | Role.Client);
        JoinedNode target = await rig.JoinAsync("Pia", Role.Client);
        Assert.True(boss.Accepted);
        Assert.True(target.Accepted);
        return new Harness { Rig = rig, Bans = bans, Boss = boss, Target = target };
    }

    /// <summary>The ban store only records a ban for a key that already has a player row (a normal join creates it).</summary>
    private static async Task BindPlayerAsync(Harness h) =>
        await h.Bans.BindAsync(h.Target.Node.Name, h.Target.Node.KeyHash, null, h.Rig.Time.GetUtcNow(), CancellationToken.None);

    private static FlatBufferBuilder Kick(int playerId, uint requestId, string reason)
    {
        var fbb = new FlatBufferBuilder(128);
        var command = new AdminCommandT
        {
            RequestKey = new Id128T { Lo = requestId, Hi = 0 },
            RequestId = requestId,
            Body = AdminBodyUnion.FromKickCmd(new KickCmdT { PlayerId = (ushort)playerId, Reason = reason }),
        };
        fbb.Finish(AdminCommand.Pack(fbb, command).Value);
        return fbb;
    }

    private static FlatBufferBuilder Ban(int playerId, uint requestId, uint durationS, string reason)
    {
        var fbb = new FlatBufferBuilder(128);
        var command = new AdminCommandT
        {
            RequestKey = new Id128T { Lo = requestId, Hi = 0 },
            RequestId = requestId,
            Body = AdminBodyUnion.FromBanCmd(new BanCmdT { PlayerId = (ushort)playerId, Reason = reason, DurationS = durationS }),
        };
        fbb.Finish(AdminCommand.Pack(fbb, command).Value);
        return fbb;
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        long until = Environment.TickCount64 + 5000;
        while (!condition())
        {
            Assert.True(Environment.TickCount64 < until, "the admin reply never arrived");
            await Task.Delay(5);
        }
    }
}

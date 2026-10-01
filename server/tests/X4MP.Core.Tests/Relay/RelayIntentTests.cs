using X4MP.Core.Relay;
using X4MP.Core.Tests.Session;
using X4MP.Proto;

namespace X4MP.Core.Tests.Relay;

/// <summary>Intents on the real actor: authority only, exactly one result, the 5 s timeout.</summary>
public class RelayIntentTests
{
    private static List<IntentResultT> Results(JoinedNode node) =>
        [.. node.Connection.SentOf(MsgType.IntentResult).Select(f => f.Decode<IntentResult>().UnPack())];

    private static List<IntentT> Intents(JoinedNode node) =>
        [.. node.Connection.SentOf(MsgType.Intent).Select(f => f.Decode<Intent>().UnPack())];

    [Fact]
    public async Task AKillClaimReachesOnlyTheAuthorityAndItsResultComesBackToTheClaimant()
    {
        await using var rig = new RelayRig();
        var authority = await rig.JoinAuthorityAsync();
        var a = await rig.JoinInGameAsync("Alice");
        var b = await rig.JoinInGameAsync("Bob");

        await rig.SendAsync(a, MsgType.Intent, RelayFrames.KillClaim(key: 11, target: 4711, requestId: 3));

        var forwarded = Assert.Single(Intents(authority));
        Assert.Equal(a.PlayerId, forwarded.PlayerId); // stamped by the server
        Assert.Equal(11ul, forwarded.RequestKey.Lo);
        Assert.Equal(3u, forwarded.RequestId);
        Assert.Equal(IntentBody.KillClaim, forwarded.Body.Type);
        Assert.Equal(4711u, forwarded.Body.AsKillClaim().Target);
        Assert.Empty(b.Connection.SentOf(MsgType.Intent));
        Assert.Empty(Intents(a));
        Assert.Empty(Results(a)); // not answered yet
        Assert.Equal(1, rig.Relay.PendingIntentCount);

        await rig.SendAsync(authority, MsgType.IntentResult, RelayFrames.IntentResult((ushort)a.PlayerId, 11, 3));

        var result = Assert.Single(Results(a));
        Assert.Equal(IntentStatus.Accepted, result.Status);
        Assert.Equal(3u, result.RequestId);
        Assert.Empty(Results(b));
        Assert.Equal(0, rig.Relay.PendingIntentCount);
    }

    [Fact]
    public async Task ASilentAuthorityGivesExactlyOneRejectedTimeout()
    {
        await using var rig = new RelayRig();
        var authority = await rig.JoinAuthorityAsync();
        var a = await rig.JoinInGameAsync("Alice");

        await rig.SendAsync(a, MsgType.Intent, RelayFrames.KillClaim(key: 21, target: 1, requestId: 8));
        await rig.AdvanceAsync(4.5);
        Assert.Empty(Results(a));

        await rig.AdvanceAsync(1);

        var timeout = Assert.Single(Results(a));
        Assert.Equal(IntentStatus.Rejected, timeout.Status);
        Assert.Equal(RejectReason.Timeout, timeout.Reason);
        Assert.Equal(21ul, timeout.RequestKey.Lo);
        Assert.Equal(8u, timeout.RequestId);
        Assert.Equal(0, rig.Relay.PendingIntentCount);

        // a late answer must not produce a second result
        await rig.SendAsync(authority, MsgType.IntentResult, RelayFrames.IntentResult((ushort)a.PlayerId, 21, 8));
        await rig.AdvanceAsync(10);

        Assert.Single(Results(a));
        Assert.Equal(1, rig.Relay.Stats.IntentResultsStale);
        Assert.Equal(1, rig.Relay.Stats.IntentTimeouts);
    }

    [Fact]
    public async Task WithoutAnAuthorityTheIntentIsRejectedAtOnce()
    {
        await using var rig = new RelayRig();
        var a = await rig.JoinInGameAsync("Alice");

        await rig.SendAsync(a, MsgType.Intent, RelayFrames.KillClaim(key: 31, target: 1));

        var result = Assert.Single(Results(a));
        Assert.Equal(RejectReason.AuthorityUnavailable, result.Reason);
        Assert.Equal(0, rig.Relay.PendingIntentCount);
    }

    [Fact]
    public async Task LosingTheAuthorityRejectsWhatWasWaitingForIt()
    {
        await using var rig = new RelayRig();
        var authority = await rig.JoinAuthorityAsync();
        var a = await rig.JoinInGameAsync("Alice");
        await rig.SendAsync(a, MsgType.Intent, RelayFrames.KillClaim(key: 41, target: 1));
        await rig.SendAsync(a, MsgType.Intent, RelayFrames.KillClaim(key: 42, target: 2));
        Assert.Equal(2, rig.Relay.PendingIntentCount);

        authority.Connection.Drop();
        long until = Environment.TickCount64 + 5000;
        while (Results(a).Count < 2)
        {
            Assert.True(Environment.TickCount64 < until, "the pending intents were never rejected");
            await Task.Delay(5);
        }

        Assert.All(Results(a), r => Assert.Equal(RejectReason.AuthorityUnavailable, r.Reason));
        Assert.Equal(0, rig.Relay.PendingIntentCount);
        await rig.AdvanceAsync(10);
        Assert.Equal(2, Results(a).Count); // the timeout does not add a second answer
    }

    [Fact]
    public async Task TooManyIntentsInFlightAreRateLimited()
    {
        await using var rig = new RelayRig(new RelayOptions { MaxPendingIntentsPerPlayer = 2 });
        var authority = await rig.JoinAuthorityAsync();
        var a = await rig.JoinInGameAsync("Alice");

        for (ulong key = 1; key <= 3; key++)
        {
            await rig.SendAsync(a, MsgType.Intent, RelayFrames.KillClaim(key, target: 5));
        }

        Assert.Equal(2, Intents(authority).Count);
        var rejected = Assert.Single(Results(a));
        Assert.Equal(RejectReason.RateLimited, rejected.Reason);
        Assert.Equal(3ul, rejected.RequestKey.Lo);
    }

    [Fact]
    public async Task AnIntentWithTheSameKeyWhilePendingIsIgnoredSoThereIsOneResult()
    {
        await using var rig = new RelayRig();
        var authority = await rig.JoinAuthorityAsync();
        var a = await rig.JoinInGameAsync("Alice");

        await rig.SendAsync(a, MsgType.Intent, RelayFrames.KillClaim(key: 51, target: 9));
        await rig.SendAsync(a, MsgType.Intent, RelayFrames.KillClaim(key: 51, target: 9));
        await rig.SendAsync(authority, MsgType.IntentResult, RelayFrames.IntentResult((ushort)a.PlayerId, 51, 1));

        Assert.Single(Intents(authority));
        Assert.Single(Results(a));
    }

    [Fact]
    public async Task AnIntentWithoutABodyIsRejectedAsInvalid()
    {
        await using var rig = new RelayRig();
        await rig.JoinAuthorityAsync();
        var a = await rig.JoinInGameAsync("Alice");

        await rig.SendAsync(a, MsgType.Intent, RelayFrames.Intent(61, 1, new IntentBodyUnion()));

        Assert.Equal(RejectReason.InvalidParameters, Assert.Single(Results(a)).Reason);
    }

    [Fact]
    public async Task AKillClaimOnAnEntityOutsideTheSendersInterestIsRejectedAndNeverForwarded()
    {
        var interest = new FakeInterest();
        await using var rig = new RelayRig(interest: interest);
        var authority = await rig.JoinAuthorityAsync();
        var a = await rig.JoinInGameAsync("Alice");

        await rig.SendAsync(a, MsgType.Intent, RelayFrames.KillClaim(key: 71, target: 4711));

        Assert.Equal(RejectReason.NotInInterest, Assert.Single(Results(a)).Reason);
        Assert.Empty(Intents(authority));

        interest.Held.Add((a.PlayerId, 4711));
        await rig.SendAsync(a, MsgType.Intent, RelayFrames.KillClaim(key: 72, target: 4711));

        Assert.Single(Intents(authority));
    }

    [Fact]
    public async Task APluggedInValidatorCanRefuseAnIntent()
    {
        await using var rig = new RelayRig();
        rig.Relay.IntentValidators.Add(new DenyAll());
        var authority = await rig.JoinAuthorityAsync();
        var a = await rig.JoinInGameAsync("Alice");

        await rig.SendAsync(a, MsgType.Intent, RelayFrames.KillClaim(key: 81, target: 1));

        Assert.Equal(RejectReason.HostileRequired, Assert.Single(Results(a)).Reason);
        Assert.Empty(Intents(authority));
    }

    private sealed class DenyAll : IIntentValidator
    {
        public RejectReason? Validate(X4MP.Core.Session.SessionNode sender, IntentT intent) => RejectReason.HostileRequired;
    }
}

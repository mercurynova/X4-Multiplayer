using X4MP.Core.Events;
using X4MP.Core.Relay;
using X4MP.Core.Teams;
using X4MP.Proto;
using static X4MP.Core.Tests.Relay.RelayRig;

namespace X4MP.Core.Tests.Relay;

/// <summary>Chat on the real actor: channels, mute, rate limit, cleanup, persistence.</summary>
public class RelayChatTests
{
    [Fact]
    public async Task ChatFromAReachesBAndCButNotAfterAIsMuted()
    {
        await using var rig = new RelayRig();
        var a = await rig.JoinInGameAsync("Alice");
        var b = await rig.JoinInGameAsync("Bob");
        var c = await rig.JoinInGameAsync("Cleo");

        await rig.ChatAsync(a, ChatChannel.All, "hello");

        foreach (var node in new[] { b, c })
        {
            var message = Assert.Single(PlayerChats(node));
            Assert.Equal("hello", message.Text);
            Assert.Equal("Alice", message.FromName);
            Assert.Equal(a.PlayerId, message.FromPlayer);
            Assert.Equal(ChatChannel.All, message.Channel);
        }

        Assert.Single(PlayerChats(a)); // the sender sees its own line, ordered with the others

        Assert.True(await rig.Relay.MuteAsync(a.PlayerId, actor: "admin"));
        await rig.ChatAsync(a, ChatChannel.All, "still there?");

        Assert.Single(PlayerChats(b));
        Assert.Single(PlayerChats(c));
        Assert.Contains(Chats(a), m => m.Channel == ChatChannel.System && m.Text.Contains("muted", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, rig.Relay.Stats.ChatMuted);

        Assert.True(await rig.Relay.UnmuteAsync(a.PlayerId));
        await rig.ChatAsync(a, ChatChannel.All, "back");
        Assert.Equal(["hello", "back"], PlayerChats(b).Select(m => m.Text));
    }

    [Fact]
    public async Task ATimedMuteEndsOnItsOwn()
    {
        await using var rig = new RelayRig();
        var a = await rig.JoinInGameAsync("Alice");
        var b = await rig.JoinInGameAsync("Bob");
        await rig.Relay.MuteAsync(a.PlayerId, TimeSpan.FromMinutes(10));

        await rig.ChatAsync(a, ChatChannel.All, "one");
        Assert.Empty(PlayerChats(b));
        Assert.True(await rig.Relay.IsMutedAsync(a.PlayerId));

        await rig.AdvanceAsync(601);
        await rig.ChatAsync(a, ChatChannel.All, "two");

        Assert.Equal(["two"], PlayerChats(b).Select(m => m.Text));
        Assert.False(await rig.Relay.IsMutedAsync(a.PlayerId));
        Assert.Null(rig.Chat.Mutes[a.PlayerId]); // the lift was persisted
    }

    [Fact]
    public async Task MutesPersistAndComeBackAfterARestart()
    {
        var store = new MemoryChatStore();
        await using (var first = new RelayRig(chat: store))
        {
            var a = await first.JoinInGameAsync("Alice");
            await first.Relay.MuteAsync(a.PlayerId, actor: "admin", reason: "spam");
            Assert.NotNull(store.Mutes[a.PlayerId]);
            store.Preloaded.Add(store.Mutes[a.PlayerId]!);
        }

        await using var second = new RelayRig(chat: store);
        var again = await second.JoinInGameAsync("Alice");
        var bob = await second.JoinInGameAsync("Bob");

        await second.ChatAsync(again, ChatChannel.All, "hi");

        Assert.Empty(PlayerChats(bob));
    }

    [Fact]
    public async Task TeamChatStaysWithinTheTeam()
    {
        await using var rig = new RelayRig(
            teams: true,
            teamOptions: new TeamOptions { AutoAssign = AutoAssignStrategy.Balance });
        Assert.True((await rig.Teams!.ApplyPresetAsync(TeamPreset.TwoTeams)).Ok);
        var a = await rig.JoinInGameAsync("Alice"); // team 1
        var b = await rig.JoinInGameAsync("Bob"); // team 2
        var c = await rig.JoinInGameAsync("Cleo"); // team 1
        Assert.Equal(rig.Teams.TeamOf(a.PlayerId), rig.Teams.TeamOf(c.PlayerId));
        Assert.NotEqual(rig.Teams.TeamOf(a.PlayerId), rig.Teams.TeamOf(b.PlayerId));

        await rig.ChatAsync(a, ChatChannel.Team, "flank left");

        Assert.Equal(["flank left"], PlayerChats(c).Select(m => m.Text));
        Assert.Equal(ChatChannel.Team, PlayerChats(c).Single().Channel);
        Assert.Single(PlayerChats(a)); // the sender's own copy
        Assert.Empty(PlayerChats(b));
        Assert.Contains(rig.Chat.Lines, l => l.Channel == "Team" && l.Text == "flank left" && l.FromPlayerId == a.PlayerId);
    }

    [Fact]
    public async Task TeamChatWithoutATeamGetsANoticeAndGoesNowhere()
    {
        await using var rig = new RelayRig();
        var a = await rig.JoinInGameAsync("Alice");
        var b = await rig.JoinInGameAsync("Bob");

        await rig.ChatAsync(a, ChatChannel.Team, "anyone?");

        Assert.Empty(PlayerChats(b));
        Assert.Contains(Chats(a), m => m.Channel == ChatChannel.System && m.Text.Contains("team", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AWhisperReachesOnlyItsTargetAndTheSender()
    {
        await using var rig = new RelayRig();
        var a = await rig.JoinInGameAsync("Alice");
        var b = await rig.JoinInGameAsync("Bob");
        var c = await rig.JoinInGameAsync("Cleo");

        await rig.ChatAsync(a, ChatChannel.Whisper, "psst", to: b.PlayerId);
        await rig.ChatAsync(a, ChatChannel.Whisper, "into the void", to: 99);

        Assert.Equal(["psst"], PlayerChats(b).Select(m => m.Text));
        Assert.Equal(["psst"], PlayerChats(a).Select(m => m.Text));
        Assert.Empty(PlayerChats(c));
        Assert.Contains(Chats(a), m => m.Channel == ChatChannel.System && m.Text.Contains("not online", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TextIsCleanedAndTruncatedAndEmptyTextIsDropped()
    {
        await using var rig = new RelayRig();
        var a = await rig.JoinInGameAsync("Alice");
        var b = await rig.JoinInGameAsync("Bob");

        await rig.ChatAsync(a, ChatChannel.All, "  he\u0007l\nlo\t ");
        await rig.ChatAsync(a, ChatChannel.All, new string('x', 400));
        await rig.ChatAsync(a, ChatChannel.All, "\u0001\u0002   ");

        var texts = PlayerChats(b).Select(m => m.Text).ToList();
        Assert.Equal(2, texts.Count);
        Assert.Equal("hello", texts[0]);
        Assert.Equal(RelayModule.MaxChatLength, texts[1].Length);
        Assert.Equal(1, rig.Relay.Stats.ChatRejected);
        Assert.Equal("ab", RelayModule.CleanChat(" a\u0000b "));
        Assert.Equal(255, RelayModule.CleanChat(new string('y', 255) + "\U0001F600").Length); // a surrogate pair is never split
    }

    [Fact]
    public async Task AFloodOfChatIsRateLimitedAndASustainedOneClosesTheNode()
    {
        await using var rig = new RelayRig(new RelayOptions { ChatBurst = 3, ChatPerSecond = 1, RateLimitHitsPerMinute = 20 });
        var a = await rig.JoinInGameAsync("Alice");
        var b = await rig.JoinInGameAsync("Bob");

        for (int i = 0; i < 10; i++)
        {
            await rig.ChatAsync(a, ChatChannel.All, "spam " + i);
        }

        Assert.Equal(3, PlayerChats(b).Count); // the burst only
        Assert.Equal(7, rig.Relay.Stats.ChatRateLimited);
        Assert.Single(Chats(a), m => m.Channel == ChatChannel.System); // one notice, not one per dropped line

        await rig.AdvanceAsync(2);
        await rig.ChatAsync(a, ChatChannel.All, "ok again");
        Assert.Equal("ok again", PlayerChats(b)[^1].Text);

        for (int i = 0; i < 40 && !a.Connection.IsClosed; i++)
        {
            await rig.ChatAsync(a, ChatChannel.All, "more spam " + i);
        }

        Assert.Equal(X4MP.Proto.DisconnectCode.RateLimited, a.Connection.CloseCode);
    }

    [Fact]
    public async Task OnlyAdminsMayUseTheAdminChannelAndPlayersCannotSpeakAsSystem()
    {
        await using var rig = new RelayRig();
        var a = await rig.JoinInGameAsync("Alice");
        var b = await rig.JoinInGameAsync("Bob");

        await rig.ChatAsync(a, ChatChannel.Admin, "I am the law");
        await rig.ChatAsync(a, ChatChannel.System, "server says");

        Assert.Empty(PlayerChats(b));
        Assert.Equal(2, rig.Relay.Stats.ChatRejected);
    }

    [Fact]
    public async Task AnAdminMessageFromTheServiceReachesEveryoneOrOnePlayerAndIsPersisted()
    {
        await using var rig = new RelayRig();
        var a = await rig.JoinInGameAsync("Alice");
        var b = await rig.JoinInGameAsync("Bob");

        Assert.Equal(2, await rig.Relay.SendAsync("jack", "server restarts in 5"));
        Assert.Equal(1, await rig.Relay.SendAsync("jack", "you are loud", ChatChannel.Admin, toPlayer: b.PlayerId));

        Assert.Equal(["server restarts in 5"], PlayerChats(a).Select(m => m.Text));
        Assert.Equal(["server restarts in 5", "you are loud"], PlayerChats(b).Select(m => m.Text));
        Assert.All(PlayerChats(b), m => Assert.Equal(ChatChannel.Admin, m.Channel));
        Assert.Equal(0, PlayerChats(b)[0].FromPlayer);
        Assert.Equal("jack", PlayerChats(b)[0].FromName);
        Assert.Equal(2, rig.Chat.Lines.Count(l => l.FromAdmin == "jack" && l.FromPlayerId is null));
    }

    [Fact]
    public async Task ChatIsPersistedAndPublished()
    {
        var store = new MemoryChatStore();
        await using var rig = new RelayRig(chat: store);
        var a = await rig.JoinInGameAsync("Alice");
        await rig.JoinInGameAsync("Bob");

        await rig.ChatAsync(a, ChatChannel.All, "persist me");

        var line = Assert.Single(store.Lines);
        Assert.Equal("persist me", line.Text);
        Assert.Equal("All", line.Channel);
        Assert.Equal(a.PlayerId, line.FromPlayerId);
        var posted = Assert.Single(rig.Events.OfType<ChatPosted>());
        Assert.Equal("persist me", posted.Text);
        Assert.Equal(a.PlayerId, posted.FromPlayerId);
    }

    [Fact]
    public async Task ChatCanBeSwitchedOff()
    {
        await using var rig = new RelayRig(new RelayOptions { ChatEnabled = false });
        var a = await rig.JoinInGameAsync("Alice");
        var b = await rig.JoinInGameAsync("Bob");

        await rig.ChatAsync(a, ChatChannel.All, "hello?");

        Assert.Empty(PlayerChats(b));
        Assert.Empty(rig.Chat.Lines);
    }
}

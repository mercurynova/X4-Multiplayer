using X4MP.Core.Relay;

namespace X4MP.Persistence.Tests;

public class SqliteChatStoreTests : TeamDbFixture
{
    [Fact]
    public async Task ChatLinesLandInChatMessagesWithPlayerOrAdminAndSession()
    {
        long session = await NewSessionAsync();
        var store = new SqliteChatStore(Factory, Writer);

        Assert.True(store.Append(new ChatLine(T0, session, Players[0], null, "All", "hello")));
        Assert.True(store.Append(new ChatLine(T0.AddSeconds(1), session, null, "jack", "Admin", "restart soon")));
        Assert.True(store.Append(new ChatLine(T0.AddSeconds(2), 0, Players[1], null, "Team", "no session yet")));
        await Writer.FlushAsync();

        Assert.Equal(3, Scalar<long>("SELECT COUNT(*) FROM chat_messages"));
        Assert.Equal("hello", Scalar<string>("SELECT text FROM chat_messages WHERE channel = 'All'"));
        Assert.Equal(Players[0], Scalar<long>("SELECT from_player_id FROM chat_messages WHERE channel = 'All'"));
        Assert.Equal(session, Scalar<long>("SELECT session_id FROM chat_messages WHERE channel = 'All'"));
        Assert.Equal("jack", Scalar<string>("SELECT from_admin FROM chat_messages WHERE channel = 'Admin'"));
        Assert.Equal(0, Scalar<long>("SELECT COUNT(*) FROM chat_messages WHERE channel = 'Admin' AND from_player_id IS NOT NULL"));
        Assert.Equal(1, Scalar<long>("SELECT COUNT(*) FROM chat_messages WHERE channel = 'Team' AND session_id IS NULL"));
    }

    [Fact]
    public async Task MutesPersistLiftAndExpire()
    {
        var store = new SqliteChatStore(Factory, Writer);

        Assert.True(store.SetMute(Players[0], new MuteEntry(Players[0], null)));
        Assert.True(store.SetMute(Players[1], new MuteEntry(Players[1], DateTimeOffset.UtcNow.AddHours(1))));
        Assert.True(store.SetMute(Players[2], new MuteEntry(Players[2], DateTimeOffset.UtcNow.AddHours(-1)))); // already over
        await Writer.FlushAsync();

        var muted = store.LoadMutes();
        Assert.Equal([Players[0], Players[1]], muted.Select(m => m.PlayerId).Order());
        Assert.Null(muted.Single(m => m.PlayerId == Players[0]).Until);
        Assert.NotNull(muted.Single(m => m.PlayerId == Players[1]).Until);

        Assert.True(store.SetMute(Players[0], null));
        await Writer.FlushAsync();

        Assert.Equal([Players[1]], store.LoadMutes().Select(m => m.PlayerId));
    }
}

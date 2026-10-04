using X4MP.Core.Session;
using X4MP.FakeNode;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.Server.Tests.Saves;

/// <summary>M3-21: an authority that rejoins fresh (no resume) after AuthorityLost is sent the current checkpoint to load, then the session runs again.</summary>
public sealed class AuthorityRejoinTests
{
    private static FakeAuthoritySaveOptions Options(SaveServer server, string dir) =>
        new() { SaveBytes = 2 * 1024L * 1024, Directory = Path.Combine(server.Dir, dir) };

    [Fact]
    public async Task AFreshAuthorityRejoinAfterAuthorityLostIsSentTheCurrentCheckpointAndTheSessionRunsAgain()
    {
        await using var server = await SaveServer.StartAsync("--X4MP:Saves:AutosaveMinutes=0");
        var first = await AuthorityRig.StartAsync(server, Options(server, "auth-1"));
        await server.WaitForAsync(s => s.Phase == SessionPhase.Running, 60_000, "first checkpoint");
        await first.WaitForCheckpointStoredAsync();
        string current = server.Saves.Status.CurrentSha256!;

        await first.DropAsync();
        await server.WaitForAsync(s => s.Phase == SessionPhase.AuthorityLost, 30_000, "authority lost");

        // the same player joins again without its resume token (the game was restarted)
        await using var second = await AuthorityRig.StartAsync(server, Options(server, "auth-2"), ready: false);
        await server.WaitForAsync(s => s.Phase == SessionPhase.AuthorityLoading, 30_000, "authority loading");
        await SaveServer.WaitUntilAsync(() => InfoFor(second) is not null, 15_000, "SessionSaveInfo for the rejoined authority");

        var info = InfoFor(second)!;
        Assert.Equal(current, Convert.ToHexStringLower([.. info.Sha256]));
        Assert.True(info.Size > 0);

        await second.ReportReadyAsync();
        await server.WaitForAsync(s => s.Phase == SessionPhase.Running, 60_000, "a new checkpoint after the rejoin");
        await second.WaitForCheckpointStoredAsync();
        await first.DisposeAsync();
    }

    private static SessionSaveInfoT? InfoFor(AuthorityRig rig)
    {
        lock (rig.Other)
        {
            foreach (var frame in rig.Other)
            {
                if (frame.Type == MsgType.SessionSaveInfo)
                {
                    return MessageRegistry.Default.Decode<SessionSaveInfo>(frame).UnPack();
                }
            }
        }

        return null;
    }
}

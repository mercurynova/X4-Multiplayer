using X4MP.Core.Saves;
using X4MP.Core.Session;
using X4MP.FakeNode;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.Server.Tests.Saves;

/// <summary>F30b: an authority that joins a session with no start save and no checkpoint is told what to do instead of waiting in silence.</summary>
public sealed class NoStartSaveNoticeTests
{
    [Fact]
    public async Task AnAuthorityJoiningASessionWithoutASaveGetsOneNoticeSayingWhatToDo()
    {
        await using var server = await SaveServer.StartAsync("--X4MP:Saves:AutosaveMinutes=0");
        var options = new FakeAuthoritySaveOptions { SaveBytes = 1024 * 1024, Directory = Path.Combine(server.Dir, "auth-1") };
        await using var rig = await AuthorityRig.StartAsync(server, options, ready: false);
        await server.WaitForAsync(s => s.Phase == SessionPhase.AuthorityLoading, 30_000, "authority loading");

        await SaveServer.WaitUntilAsync(() => Notices(rig).Count > 0, 10_000, "the no-save notice");
        var notices = Notices(rig);
        Assert.Single(notices);
        Assert.Equal(NoticeSeverity.Info, notices[0].Severity);
        Assert.Contains("no save", notices[0].Text, StringComparison.Ordinal);
        Assert.Contains("load the game", notices[0].Text, StringComparison.Ordinal);
    }

    private static List<ServerNoticeT> Notices(AuthorityRig rig)
    {
        var found = new List<ServerNoticeT>();
        lock (rig.Other)
        {
            foreach (var frame in rig.Other)
            {
                if (frame.Type == MsgType.ServerNotice)
                {
                    var notice = MessageRegistry.Default.Decode<ServerNotice>(frame).UnPack();
                    if (notice.Text == SaveService.NoStartSaveNotice)
                    {
                        found.Add(notice);
                    }
                }
            }
        }

        return found;
    }
}

using X4MP.Core.Session;
using X4MP.FakeNode;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.Server.Tests.Saves;

/// <summary>
/// M3-25 (session 4 finding 16): an authority that loads a checkpoint again finds the world mirror rolled back to that save (entities spawned after it
/// are despawned) and is told the first net id it may hand out.
/// </summary>
public sealed class AuthorityRollbackTests
{
    private static FakeAuthoritySaveOptions Options(SaveServer server, string dir) =>
        new() { SaveBytes = 2 * 1024L * 1024, Directory = Path.Combine(server.Dir, dir) };

    [Fact]
    public async Task ARejoiningAuthorityFindsTheWorldRolledBackToTheCheckpointAndGetsTheNextFreeNetId()
    {
        await using var server = await SaveServer.StartAsync("--X4MP:Saves:AutosaveMinutes=0");
        var first = await AuthorityRig.StartAsync(server, Options(server, "auth-1"));
        await server.WaitForAsync(s => s.Phase == SessionPhase.Running, 60_000, "first checkpoint");
        await first.WaitForCheckpointStoredAsync();

        uint floor = first.Authority.NetIds.NextNetId; // everything the authority allocates from here on is not in the checkpoint
        int inCheckpoint = server.World.Count;

        await first.SpawnStationsAsync(3);
        await SaveServer.WaitUntilAsync(() => server.World.Count == inCheckpoint + 3, 15_000, "three stations in the mirror");
        Assert.True(server.World.Contains(floor));

        await first.DropAsync();
        await server.WaitForAsync(s => s.Phase == SessionPhase.AuthorityLost, 30_000, "authority lost");

        await using var second = await AuthorityRig.StartAsync(server, Options(server, "auth-2"), ready: false);
        await server.WaitForAsync(s => s.Phase == SessionPhase.AuthorityLoading, 30_000, "authority loading");
        await SaveServer.WaitUntilAsync(() => AssignFor(second) is not null, 15_000, "AuthorityAssign for the rejoined authority");

        Assert.Equal(floor, AssignFor(second)!.NextNetId);
        await SaveServer.WaitUntilAsync(() => server.World.Count == inCheckpoint, 15_000, "post-checkpoint stations removed");
        Assert.DoesNotContain(server.World.All, e => e.NetId >= floor);
        await first.DisposeAsync();
    }

    private static AuthorityAssignT? AssignFor(AuthorityRig rig)
    {
        lock (rig.Other)
        {
            foreach (var frame in rig.Other)
            {
                if (frame.Type == MsgType.AuthorityAssign)
                {
                    return MessageRegistry.Default.Decode<AuthorityAssign>(frame).UnPack();
                }
            }
        }

        return null;
    }
}

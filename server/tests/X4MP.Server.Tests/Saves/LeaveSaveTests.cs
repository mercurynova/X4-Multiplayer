using X4MP.Core.Session;
using X4MP.FakeNode;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.Server.Tests.Saves;

/// <summary>F24 (ADR-054): a checkpoint before a planned authority leave.</summary>
public sealed class LeaveSaveTests
{
    private static FakeAuthoritySaveOptions Options(SaveServer server, Action<long, long>? progress = null) =>
        new() { SaveBytes = 1024 * 1024, Directory = Path.Combine(server.Dir, "fake-authority"), OnUploadProgress = progress };

    private static async Task<AuthorityRig> RunningAsync(SaveServer server, FakeAuthoritySaveOptions options)
    {
        var authority = await AuthorityRig.StartAsync(server, options);
        await server.WaitForAsync(s => s.Phase == SessionPhase.Running, 60_000, "first checkpoint");
        await authority.WaitForCheckpointStoredAsync();
        return authority;
    }

    private static Task AskAsync(AuthorityRig authority, byte kind = 0) =>
        authority.Client.SendAsync(MsgType.LeaveSaveRequest, b => LeaveSaveRequest.Pack(b, new LeaveSaveRequestT { Kind = kind }));

    private static async Task<LeaveSaveResultT> ResultAsync(AuthorityRig authority, int timeoutMs = 30_000)
    {
        LeaveSaveResultT? found = null;
        await SaveServer.WaitUntilAsync(
            () =>
            {
                lock (authority.Other)
                {
                    foreach (var frame in authority.Other)
                    {
                        if (frame.Type == MsgType.LeaveSaveResult)
                        {
                            found = MessageRegistry.Default.Decode<LeaveSaveResult>(frame).UnPack();
                            return true;
                        }
                    }
                }

                return false;
            },
            timeoutMs,
            "LeaveSaveResult");
        return found!;
    }

    private static int Results(AuthorityRig authority)
    {
        lock (authority.Other)
        {
            return authority.Other.Count(f => f.Type == MsgType.LeaveSaveResult);
        }
    }

    [Fact]
    public async Task APlannedLeaveTakesALeaveCheckpointAndAnswersOnceItIsCurrent()
    {
        await using var server = await SaveServer.StartAsync("--X4MP:Saves:AutosaveMinutes=0");
        await using var authority = await RunningAsync(server, Options(server));
        Assert.Equal(1, authority.Saves.CheckpointsStored);

        await AskAsync(authority);
        var result = await ResultAsync(authority);

        Assert.True(result.Saved, result.Detail);
        Assert.Equal(SaveReason.Leave, authority.Saves.LastRequestReason);
        Assert.Equal(2, authority.Saves.CheckpointsStored);
        // the answer comes after the new checkpoint is current, never before
        Assert.Equal(authority.Saves.LastResult!.Save.ShaHex, server.Saves.Status.CurrentSha256);
        Assert.False(server.Saves.LeaveSavePending);
    }

    [Fact]
    public async Task ARepeatedRequestWhileWaitingGetsOneAnswerAndOneCheckpoint()
    {
        await using var server = await SaveServer.StartAsync("--X4MP:Saves:AutosaveMinutes=0");
        await using var authority = await RunningAsync(server, Options(server));

        await AskAsync(authority);
        await AskAsync(authority);
        await ResultAsync(authority);
        await Task.Delay(500);

        Assert.Equal(1, Results(authority));
        Assert.Equal(2, authority.Saves.CheckpointsStored);
    }

    [Fact]
    public async Task ALeaveWhileAnotherCheckpointRunsWaitsForItAndThenTakesItsOwn()
    {
        await using var server = await SaveServer.StartAsync("--X4MP:Saves:AutosaveMinutes=0");
        await using var authority = await RunningAsync(server, Options(server));

        Assert.True(await server.Saves.RequestSaveAsync()); // an admin checkpoint is in flight (or just finished) when the leave arrives
        await AskAsync(authority);
        var result = await ResultAsync(authority);

        Assert.True(result.Saved, result.Detail);
        Assert.Equal(SaveReason.Leave, authority.Saves.LastRequestReason);
        Assert.Equal(3, authority.Saves.CheckpointsStored);
        Assert.Equal(authority.Saves.LastResult!.Save.ShaHex, server.Saves.Status.CurrentSha256);
    }

    [Fact]
    public async Task ACheckpointThatNeverFinishesIsAnsweredAsNotSavedAfterTheRequestTimeout()
    {
        using var stall = new ManualResetEventSlim(false);
        bool stalling = false;
        await using var server = await SaveServer.StartAsync("--X4MP:Saves:AutosaveMinutes=0", "--X4MP:Saves:SaveRequestTimeoutSeconds=5");
        await using var authority = await RunningAsync(
            server,
            Options(server, (_, _) =>
            {
                if (Volatile.Read(ref stalling))
                {
                    stall.Wait(TimeSpan.FromSeconds(60));
                }
            }));

        Volatile.Write(ref stalling, true);
        await AskAsync(authority);
        var result = await ResultAsync(authority, 40_000);
        stall.Set();

        Assert.False(result.Saved);
        Assert.Contains("timed out", result.Detail);
        Assert.False(server.Saves.LeaveSavePending);
    }

    [Fact]
    public async Task AnUnrelatedCheckpointThatFinishesFirstDoesNotAnswerTheLeave()
    {
        // Review 2026-10-28 (4): a manual save of the authority player (request id 0) finishes while the leave's own request is in flight. The old
        // FinishRequest cleared the in-flight request for it and answered the leave with ITS checkpoint. Here the fake authority is busy with the
        // manual save and ignores the leave's RequestSave, so the only honest answer is the request timeout.
        using var stall = new ManualResetEventSlim(false);
        bool stalling = false;
        await using var server = await SaveServer.StartAsync("--X4MP:Saves:AutosaveMinutes=0", "--X4MP:Saves:SaveRequestTimeoutSeconds=6");
        await using var authority = await RunningAsync(
            server,
            Options(server, (_, _) =>
            {
                if (Volatile.Read(ref stalling))
                {
                    stall.Wait(TimeSpan.FromSeconds(30));
                }
            }));

        Volatile.Write(ref stalling, true);
        var manual = authority.Saves.RunCheckpointAsync(0, SaveReason.Admin, resume: false);
        await SaveServer.WaitUntilAsync(() => authority.Saves.JobRunning, 10_000, "the manual save is running");
        await AskAsync(authority);
        await SaveServer.WaitUntilAsync(() => authority.Saves.LastRequestReason == SaveReason.Leave && server.Saves.LeaveSavePending, 10_000, "the leave request is in flight");
        stall.Set();
        await manual;

        await Task.Delay(1500);
        Assert.Equal(0, Results(authority)); // the manual checkpoint is not the leave's
        Assert.True(server.Saves.LeaveSavePending);

        var result = await ResultAsync(authority, 30_000);
        Assert.False(result.Saved);
        Assert.Contains("timed out", result.Detail);
    }

    [Fact]
    public async Task AnAuthorityThatDropsWhileWaitingLeavesNothingPending()
    {
        using var stall = new ManualResetEventSlim(false);
        bool stalling = false;
        await using var server = await SaveServer.StartAsync("--X4MP:Saves:AutosaveMinutes=0");
        await using var authority = await RunningAsync(
            server,
            Options(server, (_, _) =>
            {
                if (Volatile.Read(ref stalling))
                {
                    stall.Wait(TimeSpan.FromSeconds(30));
                }
            }));

        Volatile.Write(ref stalling, true);
        await AskAsync(authority);
        await SaveServer.WaitUntilAsync(() => server.Saves.LeaveSavePending, 10_000, "the leave is waiting");
        stall.Set();
        await authority.DropAsync();

        await SaveServer.WaitUntilAsync(() => !server.Saves.LeaveSavePending, 20_000, "the leave is cleared when the authority goes");
    }

    [Fact]
    public async Task AClientCannotAskForALeaveCheckpoint()
    {
        await using var server = await SaveServer.StartAsync("--X4MP:Saves:AutosaveMinutes=0");
        await using var authority = await RunningAsync(server, Options(server));
        await using var client = await ClientRig.StartAsync(server, "Late");
        await client.WaitReadyAsync(30_000);

        await client.Client.SendAsync(MsgType.LeaveSaveRequest, b => LeaveSaveRequest.Pack(b, new LeaveSaveRequestT { Kind = 0 }));
        await Task.Delay(500);

        Assert.False(server.Saves.LeaveSavePending);
        Assert.Equal(1, authority.Saves.CheckpointsStored);
    }
}

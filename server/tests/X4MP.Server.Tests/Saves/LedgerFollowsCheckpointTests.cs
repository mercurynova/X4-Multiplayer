using System.Globalization;
using System.Net;
using X4MP.Core.Economy;
using X4MP.Core.Session;
using X4MP.FakeNode;
using X4MP.Proto;
using X4MP.Protocol;
using X4MP.Server.Tests.Admin;

namespace X4MP.Server.Tests.Saves;

/// <summary>
/// ADR-053 (F13b): when the authority loads an older checkpoint the credits go back with the world. Both rollback paths over real sockets: the
/// M3-21 reload after an authority loss and the F13 resume of an Ended session.
/// </summary>
public sealed class LedgerFollowsCheckpointTests
{
    private const long TenMillion = 10_000_000;

    private static FakeAuthoritySaveOptions Options(SaveServer server, string dir) =>
        new() { SaveBytes = 2 * 1024L * 1024, Directory = Path.Combine(server.Dir, dir) };

    private static Task<long> BalanceAsync(SaveServer server, int player)
    {
        var module = server.Service<EconomyModule>();
        return server.Actor.CallAsync(() => module.Service!.Ledger.BalanceOf(module.Service.EffectiveWallet(player)));
    }

    private static Task<bool> BookAsync(SaveServer server, int player, long amount)
    {
        var module = server.Service<EconomyModule>();
        return server.Actor.CallAsync(() =>
            module.Service!.BookCreditDelta(player, true, new CreditDeltaT { Amount = amount, PlayerId = (ushort)player }).Booked);
    }

    private static Task<long> TotalAsync(SaveServer server)
    {
        var module = server.Service<EconomyModule>();
        return server.Actor.CallAsync(() => module.Service!.Ledger.TotalBalance());
    }

    [Fact]
    public async Task AnAuthorityReloadAfterALossRestoresTheCreditsSpentAndEarnedAfterTheCheckpointAndTellsTheAuthority()
    {
        await using var server = await SaveServer.StartAsync("--X4MP:Saves:AutosaveMinutes=0");
        var first = await AuthorityRig.StartAsync(server, Options(server, "auth-1"));
        await server.WaitForAsync(s => s.Phase == SessionPhase.Running, 60_000, "first checkpoint");
        await first.WaitForCheckpointStoredAsync();
        int player = first.Client.Welcome.PlayerId;

        long atCheckpoint = await BalanceAsync(server, player);

        // After the checkpoint: ten million go into ships, and some income arrives. The ships die with the crash, so must the spend.
        Assert.True(await BookAsync(server, player, -TenMillion));
        Assert.True(await BookAsync(server, player, 250_000));
        Assert.Equal(atCheckpoint - TenMillion + 250_000, await BalanceAsync(server, player));

        await first.DropAsync();
        await server.WaitForAsync(s => s.Phase == SessionPhase.AuthorityLost, 30_000, "authority lost");

        await using var second = await AuthorityRig.StartAsync(server, Options(server, "auth-2"), ready: false);
        await server.WaitForAsync(s => s.Phase == SessionPhase.AuthorityLoading, 30_000, "authority loading");
        await SaveServer.WaitUntilAsync(() => NoticeFor(second) is not null, 15_000, "the rollback notice");

        Assert.Equal(atCheckpoint, await BalanceAsync(server, player));
        Assert.Equal(0, await TotalAsync(server));
        string text = NoticeFor(second)!.Text;
        Assert.Contains("Session restored to the checkpoint of", text);
        Assert.Contains($"your balance is now {atCheckpoint.ToString("N0", CultureInfo.InvariantCulture)} Cr", text);
        // The authority was sent the restored balance: that is what its game money converges to once it has loaded.
        Assert.Equal(atCheckpoint, LastBalanceSent(second, player));
        await Admin.Api.WaitForAuditAsync(server, "economy.rollback");
        await first.DisposeAsync();
    }

    [Fact]
    public async Task AnAdminResumeOfAnEndedSessionRestoresTheCreditsToTheCheckpointAndKeepsWhatTheAdminGranted()
    {
        await using var server = await SaveServer.StartAsync("--X4MP:Saves:AutosaveMinutes=0");
        using var admin = server.Http(server.AdminToken());
        var first = await AuthorityRig.StartAsync(server, Options(server, "auth-1"));
        await server.WaitForAsync(s => s.Phase == SessionPhase.Running, 60_000, "first checkpoint");
        await first.WaitForCheckpointStoredAsync();
        int player = first.Client.Welcome.PlayerId;
        long id = await server.Actor.GetStoreSessionIdAsync();
        long atCheckpoint = await BalanceAsync(server, player);

        Assert.True(await BookAsync(server, player, -TenMillion));
        var module = server.Service<EconomyModule>();
        var granted = await server.Actor.CallAsync(() =>
            module.Service!.AdminAdjust("admin:test", module.Service.EffectiveWallet(player), 123_000, "compensation"));
        Assert.True(granted.Ok, granted.Error.ToString());

        using (var stop = await admin.CallAsync(HttpMethod.Post, $"/api/v1/sessions/{id}/stop", new { requestFinalSave = false }))
        {
            Assert.Equal(HttpStatusCode.Accepted, stop.StatusCode);
        }

        await server.WaitForAsync(s => s.Phase == SessionPhase.Ended, 30_000, "ended");
        await first.DisposeAsync();
        using (var resumed = await admin.CallAsync(HttpMethod.Post, $"/api/v1/sessions/{id}/resume", new { }))
        {
            Assert.Equal(HttpStatusCode.Accepted, resumed.StatusCode);
        }

        await using var second = await AuthorityRig.StartAsync(server, Options(server, "auth-2"), ready: false);
        await server.WaitForAsync(s => s.Phase == SessionPhase.AuthorityLoading, 30_000, "authority loading");
        await SaveServer.WaitUntilAsync(() => NoticeFor(second) is not null, 15_000, "the rollback notice");

        long expected = atCheckpoint + 123_000; // the spend is gone, the admin's grant stays
        Assert.Equal(expected, await BalanceAsync(server, player));
        Assert.Equal(expected, LastBalanceSent(second, player));
        Assert.Equal(0, await TotalAsync(server));

        // The authority then loads, stores a new checkpoint, and the books stay put.
        await second.ReportReadyAsync();
        await server.WaitForAsync(s => s.Phase == SessionPhase.Running, 60_000, "a new checkpoint after the resume");
        await second.WaitForCheckpointStoredAsync();
        Assert.Equal(expected, await BalanceAsync(server, player));
        await Admin.Api.WaitForAuditAsync(server, "economy.rollback");
    }

    private static ServerNoticeT? NoticeFor(AuthorityRig rig)
    {
        lock (rig.Other)
        {
            foreach (var frame in rig.Other)
            {
                if (frame.Type == MsgType.ServerNotice)
                {
                    var notice = MessageRegistry.Default.Decode<ServerNotice>(frame).UnPack();
                    if (notice.Text.StartsWith("Session restored", StringComparison.Ordinal))
                    {
                        return notice;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>The newest balance of the player's effective wallet in a WalletUpdate the authority received.</summary>
    private static long? LastBalanceSent(AuthorityRig rig, int player)
    {
        long? last = null;
        lock (rig.Other)
        {
            foreach (var frame in rig.Other)
            {
                if (frame.Type != MsgType.WalletUpdate)
                {
                    continue;
                }

                var update = MessageRegistry.Default.Decode<WalletUpdate>(frame).UnPack();
                foreach (var balance in update.Balances)
                {
                    if (balance.Wallet.Kind == X4MP.Proto.WalletKind.TeamShared || (balance.Wallet.Kind == X4MP.Proto.WalletKind.Player && balance.Wallet.OwnerId == player))
                    {
                        last = balance.Balance;
                    }
                }
            }
        }

        return last;
    }
}

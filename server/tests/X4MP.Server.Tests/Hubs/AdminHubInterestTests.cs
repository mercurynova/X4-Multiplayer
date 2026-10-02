using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using X4MP.Core.Interest;
using X4MP.Core.World;
using X4MP.Proto;
using X4MP.Protocol;
using X4MP.Server.Api;
using X4MP.Server.Hubs;
using X4MP.Server.Tests.Saves;

namespace X4MP.Server.Tests.Hubs;

/// <summary>Acceptance 4: a viewed sector joins the capture set the authority receives; unsubscribing or disconnecting takes it out again.</summary>
public sealed class AdminHubInterestTests
{
    private static async Task<List<ushort>> AuthorityCapturedAsync(AuthorityRig authority)
    {
        // the sectors of the newest CaptureSet the fake authority received
        Frame? last = null;
        lock (authority.Other)
        {
            foreach (var frame in authority.Other)
            {
                if (frame.Type == MsgType.CaptureSet)
                {
                    last = frame;
                }
            }
        }

        await Task.CompletedTask;
        return last is null
            ? []
            : [.. MessageRegistry.Default.Decode<CaptureSet>(last.Value).UnPack().Sectors.Select(s => s.Sector)];
    }

    private static async Task<IReadOnlyList<CaptureRate>> ServerCaptureAsync(HubRig rig)
    {
        var interest = rig.Server.Service<InterestManager>();
        return await rig.Server.Actor.CallAsync(() => (IReadOnlyList<CaptureRate>)[.. interest.LastCaptureSectors]);
    }

    private static async Task<int> AdminViewsAsync(HubRig rig)
    {
        var interest = rig.Server.Service<InterestManager>();
        return await rig.Server.Actor.CallAsync(() => interest.AdminViewCount);
    }

    [Fact]
    public async Task ASectorViewJoinsTheCaptureSetTheFakeAuthorityReceivesAndLeavesItOnUnsubscribeAndOnDisconnect()
    {
        // CaptureEvictSeconds=0: a sector nobody needs leaves the capture set at once (the default keeps it warm for 60 s)
        await using var rig = await HubRig.StartAsync("--X4MP:Interest:CaptureEvictSeconds=0", "--X4MP:Interest:CaptureSetMinIntervalMs=100");
        var authority = await rig.StartAuthorityAsync();
        var sectors = rig.Server.World.Galaxy.Current!.Sectors.Select(s => s.Index).Order().ToArray();
        ushort a = sectors[3], b = sectors[7], c = sectors[11];

        var (admin, _) = await rig.ConnectAsync();
        Assert.Empty(await ServerCaptureAsync(rig));

        await admin.InvokeAsync(AdminHubMethods.SubscribeSector, (uint)a);
        await SaveServer.WaitUntilAsync(() => AuthorityCapturedAsync(authority).GetAwaiter().GetResult().Contains(a), 10_000, "authority received the capture of the viewed sector");
        Assert.Contains(await ServerCaptureAsync(rig), s => s.Sector == a);

        // a second view is fine, a third is refused (two per admin)
        await admin.InvokeAsync(AdminHubMethods.SubscribeSector, (uint)b);
        var third = await Assert.ThrowsAsync<HubException>(() => admin.InvokeAsync(AdminHubMethods.SubscribeSector, (uint)c));
        Assert.Contains("TooManySectors", third.Message, StringComparison.Ordinal);
        await SaveServer.WaitUntilAsync(() => AuthorityCapturedAsync(authority).GetAwaiter().GetResult() is { } set && set.Contains(a) && set.Contains(b), 10_000, "both views captured");

        // invalid and unknown sectors are refused
        await Assert.ThrowsAsync<HubException>(() => admin.InvokeAsync(AdminHubMethods.SubscribeSector, 0u));
        await Assert.ThrowsAsync<HubException>(() => admin.InvokeAsync(AdminHubMethods.SubscribeSector, 60000u));

        // unsubscribe: that sector leaves the capture set, the other stays
        await admin.InvokeAsync(AdminHubMethods.UnsubscribeSector, (uint)a);
        await SaveServer.WaitUntilAsync(() => AuthorityCapturedAsync(authority).GetAwaiter().GetResult() is { } set && !set.Contains(a) && set.Contains(b), 10_000, "unsubscribed sector dropped");
        Assert.Equal(1, await AdminViewsAsync(rig));

        // disconnect: every view of the connection is removed
        await admin.StopAsync();
        await SaveServer.WaitUntilAsync(() => AdminViewsAsync(rig).GetAwaiter().GetResult() == 0, 10_000, "admin view removed on disconnect");
        await SaveServer.WaitUntilAsync(() => AuthorityCapturedAsync(authority).GetAwaiter().GetResult() is { } set && !set.Contains(b), 10_000, "disconnected admin's sector dropped");
        Assert.Empty(await ServerCaptureAsync(rig));
        Assert.Equal(0, rig.Subscriptions.SectorViewCount);
        Assert.Equal(0, rig.Subscriptions.ConnectionCount);
    }

    [Fact]
    public async Task TwoAdminsViewingTheSameSectorKeepItCapturedUntilBothLeave()
    {
        await using var rig = await HubRig.StartAsync("--X4MP:Interest:CaptureEvictSeconds=0", "--X4MP:Interest:CaptureSetMinIntervalMs=100");
        await rig.StartAuthorityAsync();
        ushort sector = rig.Server.World.Galaxy.Current!.Sectors.Select(s => s.Index).Order().ElementAt(5);

        var (first, _) = await rig.ConnectAsync();
        var (second, _) = await rig.ConnectAsync();
        await first.InvokeAsync(AdminHubMethods.SubscribeSector, (uint)sector);
        await second.InvokeAsync(AdminHubMethods.SubscribeSector, (uint)sector);
        await SaveServer.WaitUntilAsync(() => ServerCaptureAsync(rig).GetAwaiter().GetResult().Any(s => s.Sector == sector), 10_000, "captured");

        await first.StopAsync();
        await SaveServer.WaitUntilAsync(() => AdminViewsAsync(rig).GetAwaiter().GetResult() == 1, 10_000, "first view removed");
        await Task.Delay(400);
        Assert.Contains(await ServerCaptureAsync(rig), s => s.Sector == sector);

        await second.StopAsync();
        await SaveServer.WaitUntilAsync(() => ServerCaptureAsync(rig).GetAwaiter().GetResult().All(s => s.Sector != sector), 10_000, "released");
    }
}

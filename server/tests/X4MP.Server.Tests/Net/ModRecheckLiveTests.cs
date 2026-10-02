using X4MP.Core.Mods;
using X4MP.Proto;
using X4MP.Protocol;
using X4MP.Protocol.Client;
using X4MP.Server.Tests.Saves;

namespace X4MP.Server.Tests.Net;

/// <summary>
/// In AuthorityDefines mode the authority's list is the standard, so a client that connects BEFORE the authority has reported was admitted unchecked. When the authority
/// arrives the server judges those clients (docs/mod-management.md 3.4a): Strict mismatch = <c>Disconnect{ExtensionsMismatch}</c> with the exact violation, Warn = a notice.
/// </summary>
public sealed class ModRecheckLiveTests
{
    private static ExtensionInfoT Ext(string id, string version = "1.0", ExtensionClass hint = ExtensionClass.Sim) => new()
    {
        Id = id, Name = "Name of " + id, Version = version, Enabled = true, ClassHint = hint, Source = ExtensionSource.Workshop,
        WorkshopId = ModLinks.WorkshopIdOf(id), ContentHash = [], Dependencies = [],
    };

    private static async Task<Frame> WaitForFrameAsync(TcpNodeClient node, MsgType type)
    {
        using var cts = new CancellationTokenSource(10_000);
        while (true)
        {
            var frame = await node.ReceiveAsync(cts.Token) ?? throw new EndOfStreamException("the server closed the connection");
            if (frame.Type == type)
            {
                return frame;
            }
        }
    }

    [Fact]
    public async Task AClientThatJoinedBeforeTheAuthorityIsJudgedWhenTheAuthorityArrivesStrict()
    {
        await using var server = await SaveServer.StartAsync("--X4MP:Saves:AutosaveMinutes=0");
        string early = server.NextName("Early");
        string matching = server.NextName("Match");
        // both join with no authority known: admitted, nothing to compare with
        await using var mismatch = await server.ConnectAsync(early, Role.Client, extensions: [Ext("ws_1", "1.0"), Ext("ui_only", hint: ExtensionClass.ClientOnly)]);
        await using var good = await server.ConnectAsync(matching, Role.Client, extensions: [Ext("ws_2", "2.0"), Ext("ws_3", "3.0"), Ext("ego_dlc_split", "900")]);
        await server.WaitForAsync(s => s.Nodes.Count(n => n.Connected) == 2, 10_000, "both clients joined");

        // the authority has a different Sim list
        await using var authority = await server.ConnectAsync(
            server.NextName("Host"), Role.Authority | Role.Client, extensions: [Ext("ws_2", "2.0"), Ext("ws_3", "3.0"), Ext("ego_dlc_split", "900")]);

        var disconnect = MessageRegistry.Default.Decode<Disconnect>(await WaitForFrameAsync(mismatch, MsgType.Disconnect)).UnPack();
        Assert.Equal(DisconnectCode.ExtensionsMismatch, disconnect.Code);
        Assert.Contains("your mods do not match this session", disconnect.Message);
        var violation = disconnect.ModViolation;
        Assert.Equal(["ego_dlc_split", "ws_2", "ws_3"], violation.Install.Select(r => r.Id).Order(StringComparer.Ordinal)); // the DLC too (ADR-004)
        Assert.Equal(["ws_1"], violation.Disable.Select(r => r.Id));
        Assert.Equal(["3.0"], violation.Install.Where(r => r.Id == "ws_3").Select(r => r.Version));
        Assert.Equal(3ul, violation.Install.Single(r => r.Id == "ws_3").WorkshopId);

        // the slot is gone, the matching client stays, the violation is stored under the early player
        await server.WaitForAsync(s => s.Nodes.All(n => n.Name != early) && s.Nodes.Any(n => n.Name == matching && n.Connected), 10_000, "mismatching client removed");
        var report = server.Service<IModStore>().LatestReport(mismatch.Welcome.PlayerId)!;
        Assert.Equal(ModReportOutcome.Rejected, report.Outcome);
        Assert.Equal(["ws_1"], report.Violation!.Disable.Select(r => r.Id));
        Assert.Equal(ModReportOutcome.Admitted, server.Service<IModStore>().LatestReport(good.Welcome.PlayerId)!.Outcome);

        // a later join is judged by the normal path, against the authority's list
        var ex = await Assert.ThrowsAsync<HandshakeRejectedException>(async () => await (await server.ConnectAsync(server.NextName("Late"), Role.Client, extensions: [Ext("ws_1")])).DisposeAsync());
        Assert.Equal(DisconnectCode.ExtensionsMismatch, ex.Code);
    }

    [Fact]
    public async Task UnderWarnTheEarlyClientStaysAndGetsANotice()
    {
        await using var server = await SaveServer.StartAsync("--X4MP:Saves:AutosaveMinutes=0", "--X4MP:Mods:Enforcement=Warn");
        string early = server.NextName("Early");
        await using var client = await server.ConnectAsync(early, Role.Client, extensions: [Ext("ws_1")]);
        await server.WaitForAsync(s => s.Nodes.Any(n => n.Name == early && n.Connected), 10_000, "joined");

        await using var authority = await server.ConnectAsync(server.NextName("Host"), Role.Authority | Role.Client, extensions: [Ext("ws_9", "9.0")]);

        var notice = MessageRegistry.Default.Decode<ServerNotice>(await WaitForFrameAsync(client, MsgType.ServerNotice)).UnPack();
        Assert.Equal(NoticeSeverity.Warning, notice.Severity);
        Assert.Contains("install: Name of ws_9", notice.Text);
        Assert.Contains("disable: Name of ws_1", notice.Text);
        Assert.Contains(server.Actor.Snapshot.Nodes, n => n.Name == early && n.Connected); // Warn admits and flags
        Assert.Equal(ModReportOutcome.Warned, server.Service<IModStore>().LatestReport(client.Welcome.PlayerId)!.Outcome);
    }

    [Fact]
    public async Task AClientWhoseListMatchesIsLeftAlone()
    {
        await using var server = await SaveServer.StartAsync("--X4MP:Saves:AutosaveMinutes=0");
        string early = server.NextName("Early");
        await using var client = await server.ConnectAsync(early, Role.Client, extensions: [Ext("ws_5", "5.0")]);
        await server.WaitForAsync(s => s.Nodes.Any(n => n.Name == early && n.Connected), 10_000, "joined");

        await using var authority = await server.ConnectAsync(server.NextName("Host"), Role.Authority | Role.Client, extensions: [Ext("ws_5", "5.0")]);
        await server.WaitForAsync(s => s.Authority.Status == X4MP.Core.Session.AuthorityStatus.Live, 10_000, "authority live");

        Assert.Contains(server.Actor.Snapshot.Nodes, n => n.Name == early && n.Connected);
        Assert.Equal(ModReportOutcome.Admitted, server.Service<IModStore>().LatestReport(client.Welcome.PlayerId)!.Outcome);
    }
}

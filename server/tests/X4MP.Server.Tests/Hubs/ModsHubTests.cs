using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using X4MP.Core.Mods;
using X4MP.Proto;
using X4MP.Protocol;
using X4MP.Protocol.Client;
using X4MP.Server.Api;
using X4MP.Server.Auth;
using X4MP.Server.Hubs;
using X4MP.Server.Tests.Admin;

namespace X4MP.Server.Tests.Hubs;

/// <summary>
/// The mods topic of the admin hub and the policy push to nodes (M1-X4): a policy edit reaches the GUI and the connected nodes (nobody is kicked), a joining player's
/// report reaches the clients allowed to see it, and the next join is judged by the new policy.
/// </summary>
public sealed class ModsHubTests
{
    private const string Base = "/api/v1/mods";

    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static ExtensionInfoT Ext(string id, string version = "1.0", ExtensionClass hint = ExtensionClass.Sim) => new()
    {
        Id = id, Name = "Name of " + id, Version = version, Enabled = true, ClassHint = hint, Source = ExtensionSource.Workshop,
        WorkshopId = ModLinks.WorkshopIdOf(id), ContentHash = [], Dependencies = [],
    };

    private static async Task SendAsync(HttpClient admin, HttpMethod method, string url, object? body, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var response = await admin.CallAsync(method, url, body);
        Assert.True(response.StatusCode == expected, $"{method} {url}: expected {(int)expected}, got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    /// <summary>Reads node frames until one of <paramref name="type"/> arrives (10 s).</summary>
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
    public async Task APolicyEditReachesTheHubTopicAndTheNodesAndTheNextJoinUsesIt()
    {
        await using var rig = await HubRig.StartAsync();
        using var admin = rig.Server.Http(rig.Server.AdminToken());
        string alice = rig.Server.NextName("Alice");
        await using var nodeA = await rig.Server.ConnectAsync(alice, Role.Client, extensions: [Ext("ws_1", hint: ExtensionClass.ClientOnly)]);
        await rig.Server.WaitForAsync(s => s.Nodes.Any(n => n.Name == alice && n.Connected), 10_000, "alice joined");

        var (editor, editorRec) = await rig.ConnectAsync(AdminRoles.ModEditor);
        var (viewer, viewerRec) = await rig.ConnectAsync(AdminRoles.Viewer);
        var editorState = await editor.InvokeCoreAsync<ModsStateDto>(AdminHubMethods.SubscribeMods, []);
        var viewerState = await viewer.InvokeCoreAsync<ModsStateDto>(AdminHubMethods.SubscribeMods, []);
        Assert.Equal(2, rig.Subscriptions.Count(HubTopic.Mods));

        // what each may see (AdminsOnly: a Viewer never sees players' lists; a ModEditor does and may edit)
        Assert.Equal((true, false), (editorState.CanEdit, editorState.PlayersHidden));
        Assert.Equal((false, true), (viewerState.CanEdit, viewerState.PlayersHidden));
        Assert.Contains(editorState.Players, p => p.Name == alice && p.Status == "Matches");
        Assert.Empty(viewerState.Players);
        long version = editorState.Policy.Version;

        // an edit pushes ModPolicyChanged to both subscribers and the new policy to the connected node, without kicking it
        await SendAsync(admin, HttpMethod.Patch, Base + "/policy", new { sourceMode = "AdminList" });
        await SendAsync(admin, HttpMethod.Put, Base + "/entries/ws_77", new { rule = "Required", version = "1.0", name = "Seventy Seven" }, HttpStatusCode.Created);
        var forEditor = await editorRec.WaitAsync<ModPolicyDto>("ModPolicyChanged", p => p.Entries.Any(e => e.Id == "ws_77"));
        var forViewer = await viewerRec.WaitAsync<ModPolicyDto>("ModPolicyChanged", p => p.Entries.Any(e => e.Id == "ws_77"));
        Assert.True(version + 2 == forEditor.Version, $"start {version}, pushed {forEditor.Version}, entries {string.Join(",", forEditor.Entries.Select(e => e.Id))}");
        Assert.Equal(forEditor.Version, forViewer.Version);
        Assert.Equal("AdminList", forEditor.SourceMode);
        Assert.Equal(1, forEditor.Entries.Single(e => e.Id == "ws_77").PlayersMissing); // alice lacks it
        Assert.Equal(0, forViewer.Entries.Single(e => e.Id == "ws_77").PlayersMissing);   // hidden from a Viewer

        var statusPush = await editorRec.WaitAsync<PlayerModStatusDto>("PlayerModsReported", s => s.Name == alice && s.Status == "Violates");
        Assert.Equal(["ws_77"], statusPush.Violation!.Install.Select(r => r.Id));
        Assert.Equal(0, viewerRec.Count("PlayerModsReported")); // never for a Viewer under AdminsOnly

        ModPolicyT pushed;
        do
        {
            // the source-mode change may come first, with no entries yet
            pushed = MessageRegistry.Default.Decode<ModPolicyChanged>(await WaitForFrameAsync(nodeA, MsgType.ModPolicyChanged)).UnPack().Policy;
        }
        while (pushed.Entries.Count == 0);
        Assert.Equal(forEditor.Version, pushed.Version);
        Assert.Equal(["ws_77"], pushed.Entries.Select(e => e.Id));
        Assert.Contains(rig.Server.Actor.Snapshot.Nodes, n => n.Name == alice && n.Connected); // not kicked

        // the next join is judged by the new policy
        string bob = rig.Server.NextName("Bob");
        var ex = await Assert.ThrowsAsync<HandshakeRejectedException>(async () => await (await rig.Server.ConnectAsync(bob, Role.Client, extensions: [Ext("ws_1", hint: ExtensionClass.ClientOnly)])).DisposeAsync());
        Assert.Equal(DisconnectCode.ExtensionsMismatch, ex.Code);
        Assert.Equal(forEditor.Version, ex.ModViolation!.PolicyVersion);
        Assert.Equal(["ws_77"], ex.ModViolation.Install.Select(r => r.Id));
        // ...and its rejected report reaches the editor (not the viewer), with the exact violation
        var rejected = await editorRec.WaitAsync<PlayerModStatusDto>("PlayerModsReported", s => s.Name == bob);
        Assert.Equal(("Rejected", "Violates"), (rejected.Outcome, rejected.Status));
        Assert.Equal(0, viewerRec.Count("PlayerModsReported"));

        // the mod list is visible to a Viewer once the setting allows it; reports then reach it too
        await SendAsync(admin, HttpMethod.Patch, "/api/v1/settings", new Dictionary<string, string> { ["Mods.ModListVisibility"] = "AdminsAndViewers" });
        await X4MP.Server.Tests.Saves.SaveServer.WaitUntilAsync(() => rig.Server.Service<Microsoft.Extensions.Options.IOptionsMonitor<X4MP.Core.Settings.ModManagementOptions>>().CurrentValue.ModListVisibility == X4MP.Core.Settings.ModListVisibility.AdminsAndViewers, 10_000, "visibility");
        string carol = rig.Server.NextName("Carol");
        await using var nodeC = await rig.Server.ConnectAsync(carol, Role.Client, extensions: [Ext("ws_77")]);
        var carolStatus = await viewerRec.WaitAsync<PlayerModStatusDto>("PlayerModsReported", s => s.Name == carol);
        Assert.Equal(("Admitted", "Matches"), (carolStatus.Outcome, carolStatus.Status));
    }

    [Fact]
    public async Task APolicyChangeFromTheSettingsPageIsAdoptedAndPushedToo()
    {
        await using var rig = await HubRig.StartAsync();
        using var admin = rig.Server.Http(rig.Server.AdminToken());
        var (hub, rec) = await rig.ConnectAsync();
        var state = await hub.InvokeCoreAsync<ModsStateDto>(AdminHubMethods.SubscribeMods, []);
        Assert.Equal("Strict", state.Policy.Enforcement);

        await SendAsync(admin, HttpMethod.Patch, "/api/v1/settings", new Dictionary<string, string> { ["Mods.Enforcement"] = "Warn" });

        var pushed = await rec.WaitAsync<ModPolicyDto>("ModPolicyChanged", p => p.Enforcement == "Warn");
        Assert.Equal(state.Policy.Version + 1, pushed.Version);
        Assert.Equal("Warn", (await admin.GetFromJsonAsync<JsonElement>(Base)).GetProperty("policy").GetProperty("enforcement").GetString());
    }

    [Fact]
    public async Task UnsubscribingLeavesTheTopic()
    {
        await using var rig = await HubRig.StartAsync();
        var (hub, _) = await rig.ConnectAsync();
        await hub.InvokeCoreAsync<ModsStateDto>(AdminHubMethods.SubscribeMods, []);
        Assert.Equal(1, rig.Subscriptions.Count(HubTopic.Mods));
        await hub.InvokeCoreAsync(AdminHubMethods.UnsubscribeMods, []);
        Assert.Equal(0, rig.Subscriptions.Count(HubTopic.Mods));
    }
}

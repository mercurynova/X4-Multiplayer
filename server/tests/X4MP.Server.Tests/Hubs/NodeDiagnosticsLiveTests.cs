using Microsoft.AspNetCore.SignalR.Client;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using X4MP.FakeNode;
using X4MP.Server.Api;
using X4MP.Server.Hubs;
using X4MP.Server.Tests.Saves;

namespace X4MP.Server.Tests.Hubs;

/// <summary>Node diagnostics (M2-13): a FakeNode <c>--selftest</c> reaches REST and the dashboard topic of the hub within 2 s.</summary>
public sealed class NodeDiagnosticsLiveTests
{
    /// <summary>Remembers when a line containing a marker was written (FakeNode prints through <c>WriteLine</c>).</summary>
    private sealed class LineWatch : StringWriter
    {
        private long _sentAt;

        public long SentAtTimestamp => Interlocked.Read(ref _sentAt);

        public override void WriteLine(string? value)
        {
            if (value is not null && value.Contains("self-test sent", StringComparison.Ordinal))
            {
                Interlocked.CompareExchange(ref _sentAt, Stopwatch.GetTimestamp(), 0);
            }

            base.WriteLine(value);
        }
    }

    [Fact]
    public async Task AFakeNodeSelfTestShowsUpOnRestAndTheHubWithinTwoSeconds()
    {
        await using var rig = await HubRig.StartAsync();
        await rig.StartAuthorityAsync();
        using var admin = rig.Server.Http(rig.Server.AdminToken());
        var (hub, rec) = await rig.ConnectAsync();
        await hub.InvokeAsync(AdminHubMethods.SubscribeDashboard);

        string name = rig.Server.NextName("Tester");
        var options = CliParser.Parse(["client", "--name", name, "--selftest", "--duration", "60"]).Options! with { Port = rig.Server.TcpPort };
        var output = new LineWatch();
        using var cts = new CancellationTokenSource();
        var run = Task.Run(() => LiveRunner.RunAsync(options, output, new LiveRunOptions { ConnectStagger = TimeSpan.FromMilliseconds(10) }, cts.Token));
        try
        {
            var node = (await rig.Server.WaitForAsync(s => s.Nodes.Any(n => n.Name == name && n.Connected), 30_000, "tester joined")).Nodes.Single(n => n.Name == name);
            await SaveServer.WaitUntilAsync(() => output.SentAtTimestamp != 0 || run.IsCompleted, 30_000, "self-test sent");
            Assert.False(run.IsCompleted, output.ToString());

            NodeDiagnosticsDto? dto = null;
            await SaveServer.WaitUntilAsync(() =>
            {
                using var response = admin.GetAsync($"/api/v1/players/{node.PlayerId}/diagnostics").GetAwaiter().GetResult();
                if (!response.IsSuccessStatusCode)
                {
                    return false;
                }

                dto = response.Content.ReadFromJsonAsync<NodeDiagnosticsDto>(new JsonSerializerOptions(JsonSerializerDefaults.Web)).GetAwaiter().GetResult();
                return dto?.SelfTest is not null;
            }, 10_000, "self-test table on REST");
            var seen = Stopwatch.GetElapsedTime(output.SentAtTimestamp);
            Assert.True(seen < TimeSpan.FromSeconds(2), $"table took {seen.TotalMilliseconds:F0} ms after the node sent it");

            var table = dto!.SelfTest!;
            Assert.Equal("FAIL", table.Overall);
            Assert.Equal((3, 1, 1, 1), (table.Passed, table.Failed, table.Warned, table.Skipped));
            Assert.Equal(FakeSelfTest.Rows.Select(r => r.Name), table.Rows.Select(r => r.Name));
            Assert.Equal("game 9.00 x4native 9.0.0 exports 15/15", table.Rows.Single(r => r.Name == "x4native.api").Detail);
            Assert.Contains(dto.Lines, l => l.Text == "fakenode: self-test starting" && l.Level == "Info");
            Assert.Contains(dto.Lines, l => l.Level == "Warn");
            Assert.Equal(name, dto.Player);

            var pushed = await rec.WaitAsync<NodeDiagnosticsDto>("NodeDiagnosticsChanged", d => d.PlayerId == node.PlayerId && d.SelfTest is not null, 2_000);
            Assert.Equal(table.Rows.Count, pushed.SelfTest!.Rows.Count);

            // the same lines went to the server log under the node's name (the ring buffer feeds the GUI log tail)
            var ring = rig.Server.Service<X4MP.Server.Logging.RingBufferSink>();
            await SaveServer.WaitUntilAsync(
                () => ring.Snapshot().Any(e => e.RenderMessage().Contains("node:" + name, StringComparison.Ordinal) && e.RenderMessage().Contains("self-test starting", StringComparison.Ordinal)),
                5_000, "forwarded line in the server log");

            // an unknown player has nothing
            using var missing = await admin.GetAsync("/api/v1/players/99999/diagnostics");
            Assert.Equal(System.Net.HttpStatusCode.NotFound, missing.StatusCode);
        }
        finally
        {
            await cts.CancelAsync();
            await run;
        }
    }
}

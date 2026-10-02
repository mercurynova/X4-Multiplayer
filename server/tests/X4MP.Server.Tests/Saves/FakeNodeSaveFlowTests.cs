using X4MP.Core.Session;
using X4MP.FakeNode;
using X4MP.Proto;
using Xunit.Abstractions;

namespace X4MP.Server.Tests.Saves;

/// <summary>The FakeNode CLI path of M1-12: <c>fakenode swarm --with-authority</c> against the real server goes SyncingSave to InGame.</summary>
public sealed class FakeNodeSaveFlowTests(ITestOutputHelper output)
{
    private sealed class LockedWriter : TextWriter
    {
        private readonly object _gate = new();
        private readonly System.Text.StringBuilder _text = new();

        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        public override void Write(char value)
        {
            lock (_gate)
            {
                _text.Append(value);
            }
        }

        public override void Write(string? value)
        {
            lock (_gate)
            {
                _text.Append(value);
            }
        }

        public override void WriteLine(string? value)
        {
            lock (_gate)
            {
                _text.AppendLine(value);
            }
        }

        public string Snapshot()
        {
            lock (_gate)
            {
                return _text.ToString();
            }
        }
    }

    [Fact]
    public async Task SwarmWithAuthorityWalksEveryClientFromSyncingSaveToInGame()
    {
        await using var server = await SaveServer.StartAsync("--X4MP:Saves:AutosaveMinutes=0");
        var options = CliParser.Parse(["swarm", "--clients", "3", "--with-authority", "--save-mb", "3", "--sectors", "20", "--ships", "200", "--duration", "60"]).Options!
            with { Port = server.TcpPort };
        var run = new LiveRunOptions
        {
            PingInterval = TimeSpan.FromMilliseconds(100),
            ReportInterval = TimeSpan.FromMilliseconds(300),
            ConnectStagger = TimeSpan.FromMilliseconds(10),
        };
        var output1 = new LockedWriter();
        using var cts = new CancellationTokenSource();
        var runner = Task.Run(() => LiveRunner.RunAsync(options, output1, run, cts.Token));

        // the periodic report line says how many nodes are in game; the authority counts once its checkpoint is stored
        try
        {
            await SaveServer.WaitUntilAsync(() => output1.Snapshot().Contains("ingame=4", StringComparison.Ordinal), 20_000, "all four nodes in game");
        }
        catch (Xunit.Sdk.XunitException)
        {
            output.WriteLine(output1.Snapshot());
            output.WriteLine(string.Join(",", (await server.Actor.GetSnapshotAsync()).Nodes.Select(n => n.Name + ":" + n.Phase)));
            throw;
        }

        var snapshot = await server.Actor.GetSnapshotAsync();
        Assert.Equal(SessionPhase.Running, snapshot.Phase);
        Assert.Equal(4, snapshot.Nodes.Count(n => n.Phase == NodePhase.InGame));
        Assert.NotNull(server.Saves.Status.CurrentSha256);

        await cts.CancelAsync();
        int exit = await runner;
        string text = output1.Snapshot();
        output.WriteLine(text);
        Assert.Equal(0, exit);
        Assert.Contains("errors=0", text);
        Assert.Equal(3, text.Split('\n').Count(l => l.Contains("joined with the save after", StringComparison.Ordinal)));
        Assert.Contains("checkpoint stored", text);
    }

    [Fact]
    public async Task SwarmWithVerifyStillVerifiesReplicationAfterTheSaveJoin()
    {
        await using var server = await SaveServer.StartAsync("--X4MP:Saves:AutosaveMinutes=0");
        var options = CliParser.Parse(["swarm", "--clients", "2", "--with-authority", "--verify", "--save-mb", "2", "--sectors", "20", "--ships", "200", "--duration", "60"]).Options!
            with { Port = server.TcpPort };
        var run = new LiveRunOptions { PingInterval = TimeSpan.FromMilliseconds(100), ReportInterval = TimeSpan.FromMilliseconds(300), ConnectStagger = TimeSpan.FromMilliseconds(10), StopWhen = s => Net.LiveStop.Verified(s, 2, 400) };
        var text = new LockedWriter();
        using var cts = new CancellationTokenSource();
        var runner = Task.Run(() => LiveRunner.RunAsync(options, text, run, cts.Token));

        // the run ends itself once both clients are in game and have verified replication entries (StopWhen); the duration is only the upper bound
        int exit = await runner.WaitAsync(TimeSpan.FromSeconds(90));
        string result = text.Snapshot();
        output.WriteLine(result);
        Assert.Equal(0, exit);
        Assert.Contains("ingame=3", result);
        Assert.Contains("verify: clients=2", result);
        Assert.Contains("errors=0", result);
    }
}

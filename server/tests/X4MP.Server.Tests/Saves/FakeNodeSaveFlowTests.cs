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

    [Fact]
    public async Task AuthorityWithSaveFileUploadsThoseExactBytesAndTheServerStoresTheSameSha()
    {
        string dir = Path.Combine(Path.GetTempPath(), "x4mp-savefile-" + Guid.NewGuid().ToString("N"));
        try
        {
            // a ~6 MB gzip generated now, standing in for a real X4 save
            string file = FakeSaveGenerator.CreateSave(dir, 7, 1, 6L * 1024 * 1024).Path;
            string real = Path.Combine(dir, "save_001.xml.gz");
            File.Move(file, real);
            string expected = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(real)));

            await using var server = await SaveServer.StartAsync("--X4MP:Saves:AutosaveMinutes=0");
            var options = CliParser.Parse(["authority", "--save-file", real, "--sectors", "20", "--ships", "200", "--duration", "60"]).Options!
                with { Port = server.TcpPort };
            var run = new LiveRunOptions { PingInterval = TimeSpan.FromMilliseconds(100), ReportInterval = TimeSpan.FromMilliseconds(300) };
            var text = new LockedWriter();
            using var cts = new CancellationTokenSource();
            var runner = Task.Run(() => LiveRunner.RunAsync(options, text, run, cts.Token));
            try
            {
                await SaveServer.WaitUntilAsync(() => text.Snapshot().Contains("FakeNode authority: checkpoint stored (sha ", StringComparison.Ordinal), 30_000, "checkpoint stored");
            }
            catch (Xunit.Sdk.XunitException)
            {
                output.WriteLine(text.Snapshot());
                throw;
            }

            await cts.CancelAsync();
            await runner;
            output.WriteLine(text.Snapshot());
            Assert.Contains($"(sha {expected})", text.Snapshot(), StringComparison.Ordinal);
            Assert.Equal(expected, server.Saves.Status.CurrentSha256, ignoreCase: true);
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // best effort
            }
        }
    }

    [Fact]
    public void SaveFileOnAMissingFileIsAClearParseError()
    {
        var result = CliParser.Parse(["authority", "--save-file", Path.Combine(Path.GetTempPath(), "x4mp-no-such-save-" + Guid.NewGuid().ToString("N") + ".xml.gz")]);
        Assert.Null(result.Options);
        Assert.Contains("--save-file: file not found", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SwarmClientsAcceptAnAuthoritySaveFile()
    {
        string dir = Path.Combine(Path.GetTempPath(), "x4mp-savefile-" + Guid.NewGuid().ToString("N"));
        try
        {
            string real = Path.Combine(dir, "save_002.xml.gz");
            File.Move(FakeSaveGenerator.CreateSave(dir, 8, 1, 3L * 1024 * 1024).Path, real);
            await using var server = await SaveServer.StartAsync("--X4MP:Saves:AutosaveMinutes=0");
            var options = CliParser.Parse(["swarm", "--clients", "2", "--with-authority", "--save-file", real, "--sectors", "20", "--ships", "200", "--duration", "60"]).Options!
                with { Port = server.TcpPort };
            var run = new LiveRunOptions { PingInterval = TimeSpan.FromMilliseconds(100), ReportInterval = TimeSpan.FromMilliseconds(300), ConnectStagger = TimeSpan.FromMilliseconds(10) };
            var text = new LockedWriter();
            using var cts = new CancellationTokenSource();
            var runner = Task.Run(() => LiveRunner.RunAsync(options, text, run, cts.Token));
            try
            {
                await SaveServer.WaitUntilAsync(() => text.Snapshot().Contains("ingame=3", StringComparison.Ordinal), 30_000, "all nodes in game");
            }
            catch (Xunit.Sdk.XunitException)
            {
                output.WriteLine(text.Snapshot());
                throw;
            }

            await cts.CancelAsync();
            Assert.Equal(0, await runner);
            Assert.Contains("errors=0", text.Snapshot(), StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // best effort
            }
        }
    }
}

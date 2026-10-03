using System.Net;
using System.Text;
using System.Text.Json;
using X4MP.Core.Session;
using X4MP.FakeNode;
using X4MP.Proto;
using X4MP.Server.Tests.Admin;
using Xunit.Abstractions;

namespace X4MP.Server.Tests.Saves;

/// <summary>
/// M2-02: a session created from a stored save (an admin upload) gets its authority to download, verify and "load" that save before the first
/// checkpoint, and the save catalog answers right after an upload (it is written behind).
/// </summary>
public sealed class StartSaveTests(ITestOutputHelper output)
{
    private sealed class LockedWriter : TextWriter
    {
        private readonly object _gate = new();
        private readonly StringBuilder _text = new();

        public override Encoding Encoding => Encoding.UTF8;

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

    private static async Task<(long SessionId, HttpResponseMessage Created)> CreateSessionAsync(HttpClient http, string name, string saveSha)
    {
        var created = await http.CallAsync(HttpMethod.Post, "/api/v1/sessions", new { name, saveId = saveSha });
        long id = 0;
        if (created.StatusCode == HttpStatusCode.Created)
        {
            id = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetInt64();
        }

        return (id, created);
    }

    private static async Task UploadAsync(HttpClient http, FakeSaveFile save)
    {
        using var complete = await SaveAcceptanceTests.CompleteUploadAsync(http, File.ReadAllBytes(save.Path), save.ShaHex);
        Assert.True(complete.IsSuccessStatusCode, await complete.Content.ReadAsStringAsync());
    }

    private async Task<(int Exit, string Text)> RunSwarmAsync(SaveServer server, string[] args, Func<IReadOnlyList<LiveNodeStats>, bool>? stopWhen, LiveRunOptions? tuned = null)
    {
        var options = CliParser.Parse(args).Options! with { Port = server.TcpPort };
        var run = (tuned ?? new LiveRunOptions()) with
        {
            PingInterval = TimeSpan.FromMilliseconds(100),
            ReportInterval = TimeSpan.FromMilliseconds(300),
            ConnectStagger = TimeSpan.FromMilliseconds(10),
            StopWhen = stopWhen,
        };
        var text = new LockedWriter();
        using var cts = new CancellationTokenSource();
        int exit = await Task.Run(() => LiveRunner.RunAsync(options, text, run, cts.Token)).WaitAsync(TimeSpan.FromSeconds(90));
        output.WriteLine(text.Snapshot());
        return (exit, text.Snapshot());
    }

    private static FakeSaveFile FixtureSave(SaveServer server, int counter = 1) =>
        FakeSaveGenerator.CreateSave(Path.Combine(server.Dir, "scratch"), 11, counter, 3 * 1024 * 1024);

    [Fact]
    public async Task AuthorityLoadsTheUploadedSaveThenUploadsTheSessionStartCheckpointAndClientsJoinIt()
    {
        await using var server = await SaveServer.StartAsync("--X4MP:Saves:AutosaveMinutes=0");
        using var http = server.Http(server.AdminToken());
        var upload = FixtureSave(server);
        await UploadAsync(http, upload);

        // create and start straight after the upload completed: the catalog row is still on its way to the database
        var (id, created) = await CreateSessionAsync(http, "From an upload", upload.ShaHex);
        using (created)
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        using (var start = await http.CallAsync(HttpMethod.Post, $"/api/v1/sessions/{id}/start", new { }))
        {
            Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);
        }

        // stop when three clients verified replication and the session is Running (the snapshot is taken while the nodes are still connected)
        SessionSnapshot? running = null;
        var (exit, text) = await RunSwarmAsync(
            server,
            ["swarm", "--clients", "3", "--with-authority", "--expect-session-save", "--verify", "--save-mb", "2", "--sectors", "20", "--ships", "200", "--duration", "60"],
            s =>
            {
                if (running is null && Net.LiveStop.Verified(s, 3, 400) && server.Actor.Snapshot is { Phase: SessionPhase.Running } snap)
                {
                    running = snap;
                }

                return running is not null;
            });

        Assert.Equal(0, exit);
        Assert.NotNull(running);
        Assert.Equal(4, running.Nodes.Count(n => n.Phase == NodePhase.InGame));
        Assert.Contains("errors=0", text);
        Assert.Contains("authority loaded the session save", text); // it downloaded and verified the uploaded file ...
        Assert.Contains(upload.ShaHex[..12], text);
        Assert.Contains("checkpoint stored", text); // ... and then answered RequestSave{SessionStart}
        Assert.Equal(3, text.Split('\n').Count(l => l.Contains("joined with the save after", StringComparison.Ordinal)));

        // the session now runs on the authority's own checkpoint, a different file from the upload, which stays in the catalog
        string current = server.Saves.Status.CurrentSha256!;
        Assert.NotEqual(upload.ShaHex, current);
        Assert.NotNull(server.Saves.Status.CurrentManifestSha256);
        Assert.NotNull(server.Saves.Catalog.Find(upload.ShaHex));
        Assert.NotNull(server.Saves.Catalog.Find(current));
    }

    [Fact]
    public async Task AnAuthorityThatIgnoresTheSaveInfoStillGetsTheSessionRunning()
    {
        // The M1 FakeNode authority (no --expect-session-save) treats its game as loaded: the server must not insist on the download.
        await using var server = await SaveServer.StartAsync("--X4MP:Saves:AutosaveMinutes=0");
        using var http = server.Http(server.AdminToken());
        var upload = FixtureSave(server, 2);
        await UploadAsync(http, upload);
        var (id, created) = await CreateSessionAsync(http, "Ignored", upload.ShaHex);
        created.Dispose();
        using (var start = await http.CallAsync(HttpMethod.Post, $"/api/v1/sessions/{id}/start", new { }))
        {
            Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);
        }

        bool running = false;
        var (exit, text) = await RunSwarmAsync(
            server, ["swarm", "--clients", "2", "--with-authority", "--save-mb", "2", "--sectors", "20", "--ships", "200", "--duration", "60"],
            s =>
            {
                running |= Net.LiveStop.Everyone(s, 3) && server.Actor.Snapshot.Phase == SessionPhase.Running;
                return running;
            });

        Assert.Equal(0, exit);
        Assert.True(running);
        Assert.Contains("checkpoint stored", text);
        Assert.DoesNotContain("authority loaded the session save", text);
    }

    [Fact]
    public async Task ExpectSessionSaveFailsWhenTheSessionHasNoStartSave()
    {
        await using var server = await SaveServer.StartAsync("--X4MP:Saves:AutosaveMinutes=0");
        var (exit, text) = await RunSwarmAsync(
            server, ["swarm", "--clients", "1", "--with-authority", "--expect-session-save", "--sectors", "20", "--ships", "200", "--duration", "30"], null,
            new LiveRunOptions { SessionSaveInfoTimeout = TimeSpan.FromSeconds(1) });

        Assert.NotEqual(0, exit);
        Assert.Contains("SessionSaveInfo", text);
    }

    [Fact]
    public async Task ACreateRightAfterAnUploadNeverFindsTheSaveMissing()
    {
        await using var server = await SaveServer.StartAsync();
        using var http = server.Http(server.AdminToken());
        var dir = Path.Combine(server.Dir, "scratch");
        var failures = new List<string>();
        for (int i = 1; i <= 200; i++)
        {
            var save = FakeSaveGenerator.CreateSave(dir, 500, i, 2048);
            await UploadAsync(http, save);
            var (_, created) = await CreateSessionAsync(http, "Run " + i, save.ShaHex);
            using (created)
            {
                if (created.StatusCode != HttpStatusCode.Created)
                {
                    failures.Add($"#{i}: {(int)created.StatusCode} {await created.Content.ReadAsStringAsync()}");
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(5)));
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using X4MP.Core.Saves;
using X4MP.FakeNode;
using X4MP.Persistence;
using X4MP.Proto;
using Xunit.Abstractions;

namespace X4MP.Server.Tests.Saves;

/// <summary>
/// The rest of the M1-12 acceptance list against the full server: resume after a kill at 50% (download and upload), a hash mismatch, a
/// non-gzip save (in-band and 422 over HTTP), and <c>ghosts_cleaned=false</c> never becoming current.
/// </summary>
public sealed class SaveAcceptanceTests(ITestOutputHelper output)
{
    private const int Megabytes = 16;

    private static FakeAuthoritySaveOptions Options(SaveServer server, Action<long, long>? progress = null, bool corrupt = false, bool ghostsCleaned = true, FakeSaveFlavor flavor = FakeSaveFlavor.Valid) =>
        new()
        {
            SaveBytes = Megabytes * 1024L * 1024,
            Directory = Path.Combine(server.Dir, "fake-authority"),
            OnUploadProgress = progress,
            CorruptSave = corrupt,
            GhostsCleaned = ghostsCleaned,
            Flavor = flavor,
        };

    private static string[] StoredFiles(SaveServer server) => [.. Directory.GetFiles(Path.Combine(server.Dir, "saves")).Select(Path.GetFileName)!];

    // ------------------------------------------------------------------ kill at 50%: download

    [Fact]
    public async Task ClientKilledAtHalfwayResumesFromItsOffsetAndTheSecondAttemptMovesUnder60Percent()
    {
        await using var server = await SaveServer.StartAsync("--X4MP:Saves:AutosaveMinutes=0");
        await using var authority = await AuthorityRig.StartAsync(server, Options(server));
        await server.WaitForAsync(s => s.Phase == SessionPhase.Running, 60_000, "first checkpoint");
        long size = authority.Saves.LastResult!.Save.Size;

        ClientRig? rig = null;
        long atKill = -1;
        var killed = new TaskCompletionSource();
        var options = new FakeSaveClientOptions
        {
            Directory = server.ClientDir("Victim"),
            OnDownloadProgress = (done, total) =>
            {
                if (total == size && atKill < 0 && done >= size / 2)
                {
                    atKill = done;
                    rig!.Client.Abort(); // the socket dies: no Disconnect, no further acks
                    killed.TrySetResult();
                }
            },
        };
        rig = await ClientRig.StartAsync(server, "Victim", options);
        await killed.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.InRange(atKill, size / 2, size / 2 + 4 * 1024 * 1024);

        await server.WaitForAsync(s => s.Nodes.Any(n => n.Name == "Victim" && n.Phase == NodePhase.Detached), 10_000, "victim detached");
        long before = rig.Saves.BytesReceived;
        await rig.DropAsync();
        await rig.ResumeAsync();
        await rig.WaitReadyAsync(60_000);

        long second = rig.Saves.BytesReceived - before;
        double fraction = (double)second / (size + authority.Saves.LastResult.Manifest.Size);
        output.WriteLine($"download killed at {atKill} of {size} bytes; second attempt moved {second} bytes = {fraction:P0} of save + manifest");
        Assert.True(rig.Saves.StartOffset > 0 || rig.Saves.BytesReceived > 0);
        Assert.True(fraction < 0.60, $"second attempt moved {fraction:P1}");
        Assert.Equal(authority.Saves.LastResult.Save.ShaHex, rig.Saves.VerifiedSaveSha);
        Assert.Equal(authority.Saves.LastResult.Save.ShaHex, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(rig.Saves.SavePath!))));
        Assert.Equal(0, rig.Saves.ChecksumFailures);
        await rig.DisposeAsync();
    }

    // ------------------------------------------------------------------ kill at 50%: upload

    [Fact]
    public async Task AuthorityKilledAtHalfwayResumesTheUploadFromTheServersOffset()
    {
        await using var server = await SaveServer.StartAsync("--X4MP:Saves:AutosaveMinutes=0");
        AuthorityRig? rig = null;
        long killedAt = -1;
        var killed = new TaskCompletionSource();
        long size = Megabytes * 1024L * 1024;
        var options = Options(server, (done, total) =>
        {
            if (total >= size && killedAt < 0 && done >= size / 2)
            {
                killedAt = done;
                rig!.Client.Abort();
                killed.TrySetResult();
            }
        });
        rig = await AuthorityRig.StartAsync(server, options);
        await killed.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await server.WaitForAsync(s => s.Nodes.Any(n => n.Name == "Auth" && n.Phase == NodePhase.Detached), 10_000, "authority detached");
        await WaitForPartFileAsync(server);

        long sentFirst = rig.Saves.BytesSent;
        await rig.DropAsync();
        await rig.ResumeAsync(server);
        var result = await rig.Saves.RunCheckpointAsync(0, SaveReason.SessionStart, resume: true);
        await server.WaitForAsync(s => s.Phase == SessionPhase.Running, 60_000, "checkpoint after the resume");
        await rig.DisposeAsync();

        long second = rig.Saves.BytesSent - sentFirst;
        double fraction = (double)second / result.Save.Size;
        output.WriteLine($"upload killed at {killedAt} of {size}; resume offset {result.SaveResumeOffset}; second attempt sent {second} bytes = {fraction:P0} of the save");
        Assert.True(result.SaveResumeOffset >= size / 2 - 1024 * 1024, $"the server kept {result.SaveResumeOffset} bytes");
        Assert.True(fraction < 0.60, $"second attempt sent {fraction:P1}");
        Assert.Equal(SaveStoreResult.Stored, result.SaveResult);
        Assert.Equal(result.Save.ShaHex, server.Saves.Status.CurrentSha256);
        Assert.Equal(result.Save.ShaHex, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(server.Dir, "saves", result.Save.ShaHex + ".xml.gz")))));
    }

    private static async Task WaitForPartFileAsync(SaveServer server) =>
        await SaveServer.WaitUntilAsync(() => Directory.GetFiles(Path.Combine(server.Dir, "uploads"), "*.part").Length == 1, 10_000, "part file");

    // ------------------------------------------------------------------ rejections

    [Fact]
    public async Task HashMismatchIsRejectedAndNothingIsStored()
    {
        await using var server = await SaveServer.StartAsync("--X4MP:Saves:AutosaveMinutes=0");
        await using var authority = await AuthorityRig.StartAsync(server, Options(server, corrupt: true));
        await SaveServer.WaitUntilAsync(() => authority.Saves.LastResult is not null, 30_000, "upload result");

        Assert.Equal(SaveStoreResult.HashMismatch, authority.Saves.LastResult!.SaveResult);
        Assert.DoesNotContain(StoredFiles(server), f => f.EndsWith(".xml.gz", StringComparison.Ordinal));
        Assert.Null(server.Saves.Status.CurrentSha256);
        Assert.Empty(server.Service<ISaveCatalog>().List());
        Assert.Equal(SessionPhase.AuthorityLoading, (await server.Actor.GetSnapshotAsync()).Phase);
    }

    [Theory]
    [InlineData(FakeSaveFlavor.NotGzip)]
    [InlineData(FakeSaveFlavor.GzipNotASave)]
    public async Task ASaveThatIsNotAGzipSavegameIsRejectedInBand(FakeSaveFlavor flavor)
    {
        await using var server = await SaveServer.StartAsync("--X4MP:Saves:AutosaveMinutes=0");
        await using var authority = await AuthorityRig.StartAsync(server, Options(server, flavor: flavor));
        await SaveServer.WaitUntilAsync(() => authority.Saves.LastResult is not null, 30_000, "upload result");

        Assert.Equal(SaveStoreResult.NotASave, authority.Saves.LastResult!.SaveResult);
        Assert.DoesNotContain(StoredFiles(server), f => f.EndsWith(".xml.gz", StringComparison.Ordinal));
        Assert.Null(server.Saves.Status.CurrentSha256);
    }

    [Fact]
    public async Task GhostsCleanedFalseIsStoredAndFlaggedButNeverMadeCurrent()
    {
        await using var server = await SaveServer.StartAsync("--X4MP:Saves:AutosaveMinutes=0");
        await using var authority = await AuthorityRig.StartAsync(server, Options(server, ghostsCleaned: false));
        await SaveServer.WaitUntilAsync(() => authority.Saves.LastResult is not null, 30_000, "upload result");

        var result = authority.Saves.LastResult!;
        Assert.Equal(SaveStoreResult.StoredNotCurrent, result.SaveResult);
        Assert.Equal(SaveStoreResult.StoredNotCurrent, result.ManifestResult);
        Assert.Contains(result.Save.ShaHex + ".xml.gz", StoredFiles(server));
        await server.Service<PersistenceWriter>().FlushAsync();
        var row = Assert.Single(server.Service<ISaveCatalog>().List());
        Assert.False(row.GhostsCleaned);
        Assert.Null(server.Saves.Status.CurrentSha256);
        var snapshot = await server.Actor.GetSnapshotAsync();
        Assert.Equal(SessionPhase.AuthorityLoading, snapshot.Phase); // no current save: the session is not running
        using var db = server.Service<SqliteConnectionFactory>().Open();
        Assert.Equal(0, db.ExecuteScalar<int>("SELECT COUNT(*) FROM checkpoints"));
        Assert.Null(db.ExecuteScalar<long?>("SELECT current_save_id FROM sessions"));

        // a client that joins gets no save to download
        await using var client = await ClientRig.StartAsync(server, "Waiting");
        await Task.Delay(400);
        Assert.Equal(FakeJoinStage.WaitingForSaveInfo, client.Saves.Stage);
    }

    [Fact]
    public async Task OverHttpANonGzipUploadAnswers422AndAWrongHashToo()
    {
        await using var server = await SaveServer.StartAsync();
        using var http = server.Http(server.AdminToken());

        // not gzip
        byte[] plain = Encoding.UTF8.GetBytes("this is not a savegame");
        var response = await CompleteUploadAsync(http, plain, announcedSha: Convert.ToHexStringLower(SHA256.HashData(plain)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("NotASave", JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());

        // gzip of something else
        var otherDir = Path.Combine(server.Dir, "scratch");
        var notASave = FakeSaveGenerator.CreateSave(otherDir, 1, 1, 4096, flavor: FakeSaveFlavor.GzipNotASave);
        response = await CompleteUploadAsync(http, File.ReadAllBytes(notASave.Path), notASave.ShaHex);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("NotASave", JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());

        // announced hash differs from the content
        var valid = FakeSaveGenerator.CreateSave(otherDir, 1, 2, 64 * 1024);
        response = await CompleteUploadAsync(http, File.ReadAllBytes(valid.Path), new string('0', 64));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("HashMismatch", JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());

        Assert.Empty(StoredFiles(server));
    }

    /// <summary>Begins an upload with <paramref name="announcedSha"/>, sends the bytes in one piece and completes it; returns the answer to <c>complete</c>.</summary>
    internal static async Task<HttpResponseMessage> CompleteUploadAsync(HttpClient http, byte[] content, string announcedSha)
    {
        using var begin = new HttpRequestMessage(HttpMethod.Post, "/api/v1/saves/uploads")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { fileName = "test.xml.gz", size = content.Length, sha256 = announcedSha }), Encoding.UTF8, "application/json"),
        };
        begin.Headers.Add("X-X4MP", "1");
        using var started = await http.SendAsync(begin);
        Assert.Equal(HttpStatusCode.Created, started.StatusCode);
        string id = JsonDocument.Parse(await started.Content.ReadAsStringAsync()).RootElement.GetProperty("uploadId").GetString()!;

        using var put = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/saves/uploads/{id}") { Content = new ByteArrayContent(content) };
        put.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, content.Length - 1, content.Length);
        put.Headers.Add("X-X4MP", "1");
        using var put1 = await http.SendAsync(put);
        Assert.Equal(HttpStatusCode.OK, put1.StatusCode);

        using var complete = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/saves/uploads/{id}/complete");
        complete.Headers.Add("X-X4MP", "1");
        return await http.SendAsync(complete);
    }
}

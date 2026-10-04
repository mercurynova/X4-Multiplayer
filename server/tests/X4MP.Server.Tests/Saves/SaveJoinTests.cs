using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using X4MP.Core.Saves;
using X4MP.Core.Session;
using X4MP.FakeNode;
using X4MP.Proto;
using Xunit.Abstractions;

namespace X4MP.Server.Tests.Saves;

/// <summary>
/// Join-side behaviour of the save service: WorldCatchUp (the journal since the checkpoint plus a string-table replay), the
/// <c>ManifestReport</c> policy, the <c>join_checkpoint_policy</c>, the autosave cadence, the final save on stop and the HTTP fallback.
/// </summary>
public sealed class SaveJoinTests(ITestOutputHelper output)
{
    private static FakeAuthoritySaveOptions Options(SaveServer server, int megabytes = 1) =>
        new() { SaveBytes = megabytes * 1024L * 1024, Directory = Path.Combine(server.Dir, "fake-authority") };

    private static async Task<AuthorityRig> RunningAsync(SaveServer server, int sectors = 20, int ships = 200)
    {
        var authority = await AuthorityRig.StartAsync(server, Options(server), sectors: sectors, ships: ships);
        await server.WaitForAsync(s => s.Phase == SessionPhase.Running, 60_000, "first checkpoint");
        await authority.WaitForCheckpointStoredAsync();
        return authority;
    }

    // ------------------------------------------------------------------ WorldCatchUp

    [Fact]
    public async Task CatchUpReplaysTheJournalSinceTheCheckpointAndTheFullStringTable()
    {
        await using var server = await SaveServer.StartAsync("--X4MP:Saves:AutosaveMinutes=0");
        await using var authority = await AuthorityRig.StartAsync(server, Options(server), ready: false);
        await authority.SpawnStationsAsync(2); // journaled before the checkpoint: inside the save, must NOT be replayed
        await authority.ReportReadyAsync();
        await server.WaitForAsync(s => s.Phase == SessionPhase.Running, 60_000, "first checkpoint");

        await authority.SpawnStationsAsync(3); // after the SaveStarted marker: the catch-up
        await SaveServer.WaitUntilAsync(() => server.World.Journal.Entries.Count(e => e.Kind == MsgType.EntitySpawn) == 5, 10_000, "journal");

        await using var client = await ClientRig.StartAsync(server, "Late");
        await client.WaitReadyAsync(30_000);

        Assert.Equal(3, client.Saves.CatchUpEntries);
        Assert.Equal(authority.Authority.Strings.Entries.Count, client.Saves.StringEntries);
        // NodeReady is sent by the client, then the server moves the node on (CatchingUp -> InGame): wait for it instead of asserting at once
        await server.WaitForAsync(s => s.Nodes.Any(n => n.Name == "Late" && n.Phase == NodePhase.InGame), 10_000, "the late node in game");
    }

    [Fact]
    public async Task AReconnectingNodeGetsTheJournalFromItsLastSeq()
    {
        await using var server = await SaveServer.StartAsync("--X4MP:Saves:AutosaveMinutes=0");
        await using var authority = await RunningAsync(server);
        await using var client = await ClientRig.StartAsync(server, "Roamer");
        await client.WaitReadyAsync(30_000);

        ulong applied = server.World.Journal.LastSeq;
        await client.DropAsync();
        await authority.SpawnStationsAsync(4);
        await SaveServer.WaitUntilAsync(() => server.World.Journal.LastSeq == applied + 4, 10_000, "journal grows");
        long before = client.Saves.CatchUpEntries;
        await client.ResumeAsync(lastJournalSeq: applied);
        await SaveServer.WaitUntilAsync(() => client.Saves.CatchUpEntries - before == 4, 10_000, "catch-up after resume");
    }

    /// <summary>M3-14: a resume without a journal position used to get nothing at all, so a string added while the socket was down never arrived.</summary>
    [Fact]
    public async Task AResumedNodeWithoutAJournalPositionGetsTheStringsItMissed()
    {
        await using var server = await SaveServer.StartAsync("--X4MP:Saves:AutosaveMinutes=0");
        await using var authority = await RunningAsync(server);
        await using var client = await ClientRig.StartAsync(server, "Roamer");
        await client.WaitReadyAsync(30_000);
        await server.WaitForAsync(s => s.Nodes.Any(n => n.Name == "Roamer" && n.Phase == NodePhase.InGame), 10_000, "in game");

        await client.DropAsync();
        await authority.AddStringAsync("ship_arg_s_fighter_01_a_macro");
        await authority.AddStringAsync("x4mp_team_1", StringKind.Faction);
        await SaveServer.WaitUntilAsync(() => server.World.Strings.TryFind(StringKind.Faction, "x4mp_team_1", out _), 10_000, "table grows");
        Assert.DoesNotContain("x4mp_team_1", client.Saves.KnownStrings.Values);

        await client.ResumeAsync(lastJournalSeq: 0);
        await SaveServer.WaitUntilAsync(
            () => client.Saves.KnownStrings.Count == server.World.Strings.Count, 10_000, "the full table after the resume");
        Assert.Contains("ship_arg_s_fighter_01_a_macro", client.Saves.KnownStrings.Values);
        Assert.Contains("x4mp_team_1", client.Saves.KnownStrings.Values);
    }

    /// <summary>
    /// M3-14: a client that is still joining (a slow load) while the authority provisions an avatar must end up with the avatar's macro and
    /// team faction: the catch-up snapshot and the incremental send together have to cover every moment of the join.
    /// </summary>
    [Fact]
    public async Task AStringAddedWhileAClientIsStillJoiningReachesIt()
    {
        await using var server = await SaveServer.StartAsync("--X4MP:Saves:AutosaveMinutes=0");
        await using var authority = await RunningAsync(server);
        await using var client = await ClientRig.StartAsync(
            server, "Slow", new FakeSaveClientOptions { Directory = server.ClientDir("Slow"), LoadDelay = TimeSpan.FromMilliseconds(1500) });
        await server.WaitForAsync(s => s.Nodes.Any(n => n.Name == "Slow"), 15_000, "joining");
        await authority.AddStringAsync("ship_arg_s_fighter_01_a_macro");
        await authority.AddStringAsync("x4mp_team_2", StringKind.Faction);
        await client.WaitReadyAsync(30_000);
        await SaveServer.WaitUntilAsync(
            () => client.Saves.KnownStrings.Count == server.World.Strings.Count, 10_000, "the whole table, avatar strings included");
        Assert.Contains("x4mp_team_2", client.Saves.KnownStrings.Values);
    }

    // ------------------------------------------------------------------ ManifestReport policy

    [Fact]
    public async Task ManifestPolicyWarnsUpToHalfAPercentAndDisconnectsAboveIt()
    {
        await using var server = await SaveServer.StartAsync("--X4MP:Saves:AutosaveMinutes=0");
        await using var authority = await RunningAsync(server, sectors: 60, ships: 600);
        int stations = authority.Authority.World.Galaxy.StationCount;
        Assert.True(stations >= 300, $"need a few hundred manifest entries, have {stations}");
        string Dir(string n) => server.ClientDir(n);

        // one unmatched entity out of 300+ is at most 0.33%: a warning, the node carries on
        await using var tolerated = await ClientRig.StartAsync(server, "Tolerated", new FakeSaveClientOptions { Directory = Dir("Tolerated"), UnmatchedFraction = 1.0 / stations });
        await tolerated.WaitReadyAsync(30_000);
        Assert.Equal(DisconnectCode.None, tolerated.DisconnectCode);

        // 2% is above the limit: ManifestMismatch
        await using var refused = await ClientRig.StartAsync(server, "Refused", new FakeSaveClientOptions { Directory = Dir("Refused"), UnmatchedFraction = 0.02 });
        await SaveServer.WaitUntilAsync(() => refused.DisconnectCode == DisconnectCode.ManifestMismatch, 15_000, "ManifestMismatch");
        var snapshot = await server.Actor.GetSnapshotAsync();
        Assert.DoesNotContain(snapshot.Nodes, n => n.Name == "Refused");
        Assert.Contains(snapshot.Nodes, n => n.Name == "Tolerated" && n.Phase == NodePhase.InGame);
    }

    // ------------------------------------------------------------------ join_checkpoint_policy

    [Fact]
    public async Task LatestPlusJournalGivesTheNewestCheckpointWithoutAskingForAFreshSave()
    {
        await using var server = await SaveServer.StartAsync("--X4MP:Saves:AutosaveMinutes=0", "--X4MP:Saves:FreshSaveJournalEntries=1");
        await using var authority = await RunningAsync(server);
        await authority.SpawnStationsAsync(5);
        await SaveServer.WaitUntilAsync(() => server.World.Journal.LastSeq >= 6, 10_000, "journal");

        await using var client = await ClientRig.StartAsync(server, "Plain");
        await client.WaitReadyAsync(30_000);
        Assert.Equal(1, authority.Saves.CheckpointsStored);
        Assert.Equal(authority.Saves.LastResult!.Save.ShaHex, client.Saves.VerifiedSaveSha);
        Assert.Equal(5, client.Saves.CatchUpEntries);
    }

    [Fact]
    public async Task FreshSavePolicyAsksTheAuthorityForANewCheckpointWhenTheJournalIsLong()
    {
        await using var server = await SaveServer.StartAsync(
            "--X4MP:Saves:AutosaveMinutes=0", "--X4MP:Saves:JoinCheckpointPolicy=FreshSave", "--X4MP:Saves:FreshSaveJournalEntries=2", "--X4MP:Saves:FreshSaveMaxAgeMinutes=1440");
        await using var authority = await RunningAsync(server);
        string first = authority.Saves.LastResult!.Save.ShaHex;
        await authority.SpawnStationsAsync(5);
        await SaveServer.WaitUntilAsync(() => server.World.Journal.LastSeq >= 6, 10_000, "journal");

        await using var client = await ClientRig.StartAsync(server, "Fresh");
        await client.WaitReadyAsync(30_000);
        Assert.Equal(2, authority.Saves.CheckpointsStored);
        string second = authority.Saves.LastResult!.Save.ShaHex;
        Assert.NotEqual(first, second);
        Assert.Equal(second, client.Saves.VerifiedSaveSha);
        Assert.Equal(0, client.Saves.CatchUpEntries); // everything is inside the fresh save
    }

    [Fact]
    public async Task FreshSavePolicyKeepsTheCurrentCheckpointWhileTheJournalIsShort()
    {
        await using var server = await SaveServer.StartAsync(
            "--X4MP:Saves:AutosaveMinutes=0", "--X4MP:Saves:JoinCheckpointPolicy=FreshSave", "--X4MP:Saves:FreshSaveJournalEntries=100", "--X4MP:Saves:FreshSaveMaxAgeMinutes=1440");
        await using var authority = await RunningAsync(server);
        await using var client = await ClientRig.StartAsync(server, "Short");
        await client.WaitReadyAsync(30_000);
        Assert.Equal(1, authority.Saves.CheckpointsStored);
        Assert.Equal(authority.Saves.LastResult!.Save.ShaHex, client.Saves.VerifiedSaveSha);
    }

    // ------------------------------------------------------------------ cadence, compaction, admin request

    [Fact]
    public async Task AutosaveAsksForCheckpointsOnTheConfiguredCadenceAndTheJournalIsCompactedBeforeThePreviousOne()
    {
        await using var server = await SaveServer.StartAsync("--X4MP:Saves:AutosaveMinutes=0.03"); // 1.8 s
        await using var authority = await RunningAsync(server);
        await SaveServer.WaitUntilAsync(() => authority.Saves.CheckpointsStored >= 3, 30_000, "three checkpoints");

        var markers = server.World.Journal.Markers;
        Assert.True(markers.Count <= 3, "old markers are compacted");
        string current = authority.Saves.LastResult!.Save.ShaHex;
        await SaveServer.WaitUntilAsync(() => server.Saves.Status.CurrentSha256 == current || authority.Saves.CheckpointsStored > 3, 10_000, "current moves along");
        Assert.NotNull(server.Saves.Status.CurrentSha256);
        output.WriteLine($"checkpoints stored: {authority.Saves.CheckpointsStored}; markers kept: {markers.Count}");
    }

    [Fact]
    public async Task WithoutAnAutosaveNothingIsRequestedUntilAnAdminAsks()
    {
        await using var server = await SaveServer.StartAsync("--X4MP:Saves:AutosaveMinutes=0");
        await using var authority = await RunningAsync(server);
        await Task.Delay(1500);
        Assert.Equal(1, authority.Saves.CheckpointsStored);

        Assert.True(await server.Saves.RequestSaveAsync());
        await SaveServer.WaitUntilAsync(() => authority.Saves.CheckpointsStored == 2, 20_000, "admin checkpoint");
        Assert.Equal(authority.Saves.LastResult!.Save.ShaHex, server.Saves.Status.CurrentSha256);
    }

    // ------------------------------------------------------------------ stop

    [Fact]
    public async Task StoppingRequestsAFinalSaveAndThenEndsTheSessionWithoutWaitingForTheTimeout()
    {
        await using var server = await SaveServer.StartAsync("--X4MP:Saves:AutosaveMinutes=0", "--X4MP:Session:StoppingTimeoutSeconds=120");
        await using var authority = await RunningAsync(server);
        Assert.Equal(1, authority.Saves.CheckpointsStored);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var result = await server.Actor.ApplyAsync(SessionTrigger.Stop, "test stop");
        Assert.True(result.Applied);
        await server.WaitForAsync(s => s.Phase == SessionPhase.Ended, 30_000, "session ended by the save service");

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), "ended long before the 120 s stop timeout");
        await authority.WaitForCheckpointStoredAsync(2); // the session ends when the server stored the final save; the authority reads its SaveStored a moment later
        Assert.Equal(2, authority.Saves.CheckpointsStored); // the final save was uploaded
        output.WriteLine($"final save + StopCompleted in {clock.Elapsed.TotalMilliseconds:F0} ms");
    }

    [Fact]
    public async Task StoppingWithoutAnAuthorityEndsAtOnce()
    {
        await using var server = await SaveServer.StartAsync("--X4MP:Session:StoppingTimeoutSeconds=120");
        Assert.True((await server.Actor.StartAsync("empty")).Applied);
        Assert.True((await server.Actor.ApplyAsync(SessionTrigger.Stop)).Applied);
        await server.WaitForAsync(s => s.Phase == SessionPhase.Ended, 10_000, "ended without an authority to wait for");
    }

    // ------------------------------------------------------------------ HTTP fallback

    [Fact]
    public async Task HttpFallbackServesTheSaveWithTokenRangeAndETag()
    {
        await using var server = await SaveServer.StartAsync("--X4MP:Saves:AutosaveMinutes=0");
        await using var authority = await RunningAsync(server);
        string sha = authority.Saves.LastResult!.Save.ShaHex;
        byte[] expected = File.ReadAllBytes(authority.Saves.LastResult.Save.Path);

        await using var client = await ClientRig.StartAsync(server, "Http", caps: (ulong)Capability.SaveHttp);
        await client.WaitReadyAsync(30_000);
        var info = client.Saves.SaveInfo!;
        Assert.EndsWith("/files/saves/" + sha, info.HttpUrl, StringComparison.Ordinal);
        Assert.EndsWith("/files/saves/" + authority.Saves.LastResult.Manifest.ShaHex, info.ManifestHttpUrl, StringComparison.Ordinal);
        Assert.Equal(32, info.DownloadToken.Length);

        using var http = server.Http();
        HttpRequestMessage Get(string path, string? token = null, Action<HttpRequestMessage>? tweak = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, path);
            if (token is not null)
            {
                request.Headers.TryAddWithoutValidation("Authorization", "X4MP-Download " + token);
            }

            tweak?.Invoke(request);
            return request;
        }

        // the whole file
        using (var full = await http.SendAsync(Get(info.HttpUrl, info.DownloadToken)))
        {
            Assert.Equal(HttpStatusCode.OK, full.StatusCode);
            Assert.Equal($"\"{sha}\"", full.Headers.ETag!.Tag);
            Assert.Contains("bytes", full.Headers.AcceptRanges);
            Assert.Equal(expected, await full.Content.ReadAsByteArrayAsync());
        }

        // a range
        using (var range = await http.SendAsync(Get(info.HttpUrl, info.DownloadToken, r => r.Headers.Range = new RangeHeaderValue(100, 199))))
        {
            Assert.Equal(HttpStatusCode.PartialContent, range.StatusCode);
            Assert.Equal(expected.AsSpan(100, 100).ToArray(), await range.Content.ReadAsByteArrayAsync());
            Assert.Equal(100, range.Content.Headers.ContentRange!.From);
            Assert.Equal(expected.Length, range.Content.Headers.ContentRange.Length);
        }

        // If-Range with the right ETag keeps the range, with a stale one the whole file comes back
        using (var matching = await http.SendAsync(Get(info.HttpUrl, info.DownloadToken, r =>
        {
            r.Headers.Range = new RangeHeaderValue(10, 19);
            r.Headers.IfRange = new RangeConditionHeaderValue(new EntityTagHeaderValue($"\"{sha}\""));
        })))
        {
            Assert.Equal(HttpStatusCode.PartialContent, matching.StatusCode);
        }

        using (var stale = await http.SendAsync(Get(info.HttpUrl, info.DownloadToken, r =>
        {
            r.Headers.Range = new RangeHeaderValue(10, 19);
            r.Headers.IfRange = new RangeConditionHeaderValue(new EntityTagHeaderValue("\"stale\""));
        })))
        {
            Assert.Equal(HttpStatusCode.OK, stale.StatusCode);
            Assert.Equal(expected.Length, (await stale.Content.ReadAsByteArrayAsync()).Length);
        }

        // the manifest is served the same way
        using (var manifest = await http.SendAsync(Get(info.ManifestHttpUrl, info.DownloadToken)))
        {
            Assert.Equal(HttpStatusCode.OK, manifest.StatusCode);
            Assert.Equal(authority.Saves.LastResult.Manifest.ShaHex, Convert.ToHexStringLower(SHA256.HashData(await manifest.Content.ReadAsByteArrayAsync())));
        }

        // no credential, a wrong token, an unknown hash
        using (var none = await http.SendAsync(Get(info.HttpUrl)))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
        }

        using (var wrong = await http.SendAsync(Get(info.HttpUrl, new string('a', 32))))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        using (var unknown = await http.SendAsync(Get("/files/saves/" + new string('b', 64), info.DownloadToken)))
        {
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        }

        using (var traversal = await http.SendAsync(Get("/files/saves/..%2f..%2fx4mp.db", info.DownloadToken)))
        {
            Assert.NotEqual(HttpStatusCode.OK, traversal.StatusCode);
        }
    }

    [Fact]
    public async Task HttpFallbackAcceptsAnAdminOrViewerCredentialAndTheTokenDiesWhenThePlayerLeaves()
    {
        await using var server = await SaveServer.StartAsync("--X4MP:Saves:AutosaveMinutes=0");
        await using var authority = await RunningAsync(server);
        string sha = authority.Saves.LastResult!.Save.ShaHex;

        using (var viewer = server.Http(server.AdminToken(X4MP.Server.Auth.AdminRoles.Viewer)))
        using (var ok = await viewer.GetAsync("/files/saves/" + sha, HttpCompletionOption.ResponseHeadersRead))
        {
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        string token;
        await using (var client = await ClientRig.StartAsync(server, "Leaver", caps: (ulong)Capability.SaveHttp))
        {
            await client.WaitReadyAsync(30_000);
            token = client.Saves.SaveInfo!.DownloadToken;
            Assert.True(server.Saves.Tokens.TryValidate(token, out int player));
            Assert.True(player > 0);
            await client.LeaveAsync();
        }

        await server.WaitForAsync(s => s.Nodes.All(n => n.Name != "Leaver"), 10_000, "player left");
        Assert.False(server.Saves.Tokens.TryValidate(token, out _));

        // a node without the capability is not offered HTTP at all
        await using var plain = await ClientRig.StartAsync(server, "NoHttp");
        await plain.WaitReadyAsync(30_000);
        Assert.Equal(string.Empty, plain.Saves.SaveInfo!.HttpUrl ?? string.Empty);
        Assert.Equal(string.Empty, plain.Saves.SaveInfo.DownloadToken ?? string.Empty);
    }

    [Fact]
    public async Task AdminUploadIsResumableListedDownloadableAndDeletable()
    {
        await using var server = await SaveServer.StartAsync();
        using var http = server.Http(server.AdminToken());
        var save = FakeSaveGenerator.CreateSave(Path.Combine(server.Dir, "scratch"), 7, 1, 300_000);
        byte[] bytes = File.ReadAllBytes(save.Path);

        using var begin = new HttpRequestMessage(HttpMethod.Post, "/api/v1/saves/uploads")
        {
            Content = new StringContent($"{{\"fileName\":\"hand made.xml.gz\",\"size\":{bytes.Length},\"sha256\":\"{save.ShaHex}\"}}", System.Text.Encoding.UTF8, "application/json"),
        };
        begin.Headers.Add("X-X4MP", "1");
        using var started = await http.SendAsync(begin);
        Assert.Equal(HttpStatusCode.Created, started.StatusCode);
        string id = System.Text.Json.JsonDocument.Parse(await started.Content.ReadAsStringAsync()).RootElement.GetProperty("uploadId").GetString()!;

        async Task<HttpResponseMessage> PutAsync(int from, int to)
        {
            var put = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/saves/uploads/{id}") { Content = new ByteArrayContent(bytes[from..(to + 1)]) };
            put.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, bytes.Length);
            put.Headers.Add("X-X4MP", "1");
            return await http.SendAsync(put);
        }

        // first half, then a retry of the same range (409 tells where the server is), then the rest
        int half = bytes.Length / 2;
        using (var first = await PutAsync(0, half - 1))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        using (var progress = await http.GetAsync($"/api/v1/saves/uploads/{id}"))
        {
            var body = System.Text.Json.JsonDocument.Parse(await progress.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal(half, body.GetProperty("receivedBytes").GetInt64());
        }

        using (var again = await PutAsync(0, half - 1))
        {
            Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        }

        using (var second = await PutAsync(half, bytes.Length - 1))
        {
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        }

        using var complete = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/saves/uploads/{id}/complete");
        complete.Headers.Add("X-X4MP", "1");
        using var done = await http.SendAsync(complete);
        Assert.Equal(HttpStatusCode.Created, done.StatusCode);
        Assert.True(File.Exists(Path.Combine(server.Dir, "saves", save.ShaHex + ".xml.gz")));

        await server.Service<X4MP.Persistence.PersistenceWriter>().FlushAsync();
        using (var list = await http.GetAsync("/api/v1/saves"))
        {
            var items = System.Text.Json.JsonDocument.Parse(await list.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal(1, items.GetArrayLength());
            Assert.Equal(save.ShaHex, items[0].GetProperty("sha256").GetString());
            Assert.Equal("hand made", items[0].GetProperty("displayName").GetString());
            Assert.Equal("admin-upload", items[0].GetProperty("source").GetString());
        }

        using (var download = await http.GetAsync($"/api/v1/saves/{save.ShaHex}/download"))
        {
            Assert.Equal(bytes, await download.Content.ReadAsByteArrayAsync());
        }

        using var delete = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/saves/{save.ShaHex}");
        delete.Headers.Add("X-X4MP", "1");
        using var deleted = await http.SendAsync(delete);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.False(File.Exists(Path.Combine(server.Dir, "saves", save.ShaHex + ".xml.gz")));
    }
}

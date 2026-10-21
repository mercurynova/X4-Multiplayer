using System.Globalization;
using System.Net;
using X4MP.Core.Session;
using X4MP.FakeNode;
using X4MP.Proto;
using X4MP.Protocol;
using X4MP.Server.Tests.Admin;

namespace X4MP.Server.Tests.Saves;

/// <summary>F13 (ADR-052): an admin resumes an Ended session from its last checkpoint and the authority that comes back loads it.</summary>
public sealed class AuthorityResumeEndedTests
{
    private static FakeAuthoritySaveOptions Options(SaveServer server, string dir) =>
        new() { SaveBytes = 2 * 1024L * 1024, Directory = Path.Combine(server.Dir, dir) };

    [Fact]
    public async Task AnEndedSessionIsResumedByTheAdminAndTheReturningAuthorityLoadsTheLastCheckpoint()
    {
        await using var server = await SaveServer.StartAsync("--X4MP:Saves:AutosaveMinutes=0");
        using var admin = server.Http(server.AdminToken());
        using var viewer = server.Http(server.AdminToken("Viewer"));
        var first = await AuthorityRig.StartAsync(server, Options(server, "auth-1"));
        await server.WaitForAsync(s => s.Phase == SessionPhase.Running, 60_000, "first checkpoint");
        await first.WaitForCheckpointStoredAsync();
        string checkpoint = server.Saves.Status.CurrentSha256!;
        long id = await server.Actor.GetStoreSessionIdAsync();

        // Not ended yet: refused.
        using (var early = await admin.CallAsync(HttpMethod.Post, $"/api/v1/sessions/{id}/resume", new { }))
        {
            await early.AssertProblemAsync(HttpStatusCode.Conflict, "SessionNotEnded");
        }

        // The admin ends the session without a final save (the effect of an expired authority grace).
        using (var stop = await admin.CallAsync(HttpMethod.Post, $"/api/v1/sessions/{id}/stop", new { requestFinalSave = false }))
        {
            Assert.Equal(HttpStatusCode.Accepted, stop.StatusCode);
        }

        await server.WaitForAsync(s => s.Phase == SessionPhase.Ended, 30_000, "ended");
        await first.DisposeAsync();
        Assert.Equal(checkpoint, server.Saves.Status.CurrentSha256); // the checkpoint survives the end

        using (var denied = await viewer.CallAsync(HttpMethod.Post, $"/api/v1/sessions/{id}/resume", new { }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }

        using (var wrong = await admin.CallAsync(HttpMethod.Post, "/api/v1/sessions/987654/resume", new { }))
        {
            await wrong.AssertProblemAsync(HttpStatusCode.NotFound, "NotFound");
        }

        using (var resumed = await admin.CallAsync(HttpMethod.Post, $"/api/v1/sessions/{id}/resume", new { }))
        {
            Assert.Equal(HttpStatusCode.Accepted, resumed.StatusCode);
            var detail = await resumed.JsonAsync();
            Assert.Equal("WaitingForAuthority", detail.GetProperty("state").GetString());
            Assert.Equal(id, detail.GetProperty("id").GetInt64());
        }

        await Admin.Api.WaitForAuditAsync(server, "session.resume", id.ToString(CultureInfo.InvariantCulture));

        // The authority's game restarted: it joins fresh, is sent the last checkpoint, loads it and the session runs on a new checkpoint.
        await using var second = await AuthorityRig.StartAsync(server, Options(server, "auth-2"), ready: false);
        await server.WaitForAsync(s => s.Phase == SessionPhase.AuthorityLoading, 30_000, "authority loading");
        await SaveServer.WaitUntilAsync(() => InfoFor(second) is not null, 15_000, "SessionSaveInfo for the returning authority");
        Assert.Equal(checkpoint, Convert.ToHexStringLower([.. InfoFor(second)!.Sha256]));

        await second.ReportReadyAsync();
        await server.WaitForAsync(s => s.Phase == SessionPhase.Running, 60_000, "a new checkpoint after the resume");
        await second.WaitForCheckpointStoredAsync();
        Assert.Equal(id, await server.Actor.GetStoreSessionIdAsync());
    }

    [Fact]
    public async Task ResumeIsRefusedWhenTheSessionEndedBeforeAnyCheckpoint()
    {
        await using var server = await SaveServer.StartAsync();
        using var admin = server.Http(server.AdminToken());
        long id;
        using (var created = await admin.CallAsync(HttpMethod.Post, "/api/v1/sessions", new { name = "Empty" }))
        {
            id = (await created.JsonAsync()).GetProperty("id").GetInt64();
        }

        using (var started = await admin.CallAsync(HttpMethod.Post, $"/api/v1/sessions/{id}/start", new { }))
        {
            Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        }

        using (var stopped = await admin.CallAsync(HttpMethod.Post, $"/api/v1/sessions/{id}/stop", new { requestFinalSave = false }))
        {
            Assert.Equal(HttpStatusCode.Accepted, stopped.StatusCode);
        }

        using var resume = await admin.CallAsync(HttpMethod.Post, $"/api/v1/sessions/{id}/resume", new { });
        await resume.AssertProblemAsync(HttpStatusCode.Conflict, "NoCheckpoint");
    }

    private static SessionSaveInfoT? InfoFor(AuthorityRig rig)
    {
        lock (rig.Other)
        {
            foreach (var frame in rig.Other)
            {
                if (frame.Type == MsgType.SessionSaveInfo)
                {
                    return MessageRegistry.Default.Decode<SessionSaveInfo>(frame).UnPack();
                }
            }
        }

        return null;
    }
}

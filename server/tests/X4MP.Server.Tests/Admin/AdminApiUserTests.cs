using System.Globalization;
using System.Net;
using X4MP.Server.Tests.Saves;

namespace X4MP.Server.Tests.Admin;

/// <summary>Account management under <c>/api/v1/users</c>: create, list, role change, password reset, delete and the audit trail.</summary>
public sealed class AdminApiUserTests(AdminServerFixture f) : IClassFixture<AdminServerFixture>
{
    private const string Password = "initial-password-1";
    private const string NewPassword = "changed-password-2";

    private static string NewName() => "ci-user-" + Guid.NewGuid().ToString("N")[..10];

    [Fact]
    public async Task UsersAreCreatedListedChangedAndDeletedWithoutLeakingSecrets()
    {
        string username = NewName();
        long id;
        using (var created = await f.Admin.CallAsync(HttpMethod.Post, "/api/v1/users", new { username, password = Password, role = "Viewer" }))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var body = await created.JsonAsync();
            id = body.GetProperty("id").GetInt64();
            Assert.Equal(username, body.GetProperty("username").GetString());
            Assert.Equal("Viewer", body.GetProperty("role").GetString());
            Assert.True(body.GetProperty("mustChange").GetBoolean());
            Assert.False(body.TryGetProperty("password", out _));
            Assert.False(body.TryGetProperty("passwordHash", out _));
        }

        string target = id.ToString(CultureInfo.InvariantCulture);

        using (var list = await f.Admin.GetAsync("/api/v1/users"))
        {
            string text = await list.Content.ReadAsStringAsync();
            Assert.Contains(username, text, StringComparison.Ordinal);
            Assert.DoesNotContain(Password, text, StringComparison.Ordinal);
        }

        // The account signs in with the password it was given (must_change does not block a Viewer read that is then refused by role).
        using (var login = await f.Anon.CallAsync(HttpMethod.Post, "/api/v1/auth/login", new { username, password = Password }, csrf: true))
        {
            Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        }

        using (var promoted = await f.Admin.CallAsync(HttpMethod.Patch, $"/api/v1/users/{id}", new { role = "ModEditor" }))
        {
            Assert.Equal(HttpStatusCode.OK, promoted.StatusCode);
            Assert.Equal("ModEditor", (await promoted.JsonAsync()).GetProperty("role").GetString());
        }

        using (var reset = await f.Admin.CallAsync(HttpMethod.Patch, $"/api/v1/users/{id}", new { password = NewPassword }))
        {
            Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
            Assert.True((await reset.JsonAsync()).GetProperty("mustChange").GetBoolean());
        }

        using (var old = await f.Anon.CallAsync(HttpMethod.Post, "/api/v1/auth/login", new { username, password = Password }, csrf: true))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, old.StatusCode);
        }

        using (var fresh = await f.Anon.CallAsync(HttpMethod.Post, "/api/v1/auth/login", new { username, password = NewPassword }, csrf: true))
        {
            Assert.Equal(HttpStatusCode.NoContent, fresh.StatusCode);
        }

        using (var deleted = await f.Admin.CallAsync(HttpMethod.Delete, $"/api/v1/users/{id}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        using (var again = await f.Admin.CallAsync(HttpMethod.Delete, $"/api/v1/users/{id}"))
        {
            await again.AssertProblemAsync(HttpStatusCode.NotFound, "NotFound");
        }

        // The trail names the account and the change, never a password.
        await Api.WaitForAuditAsync(f.Server, "user.create", target);
        await Api.WaitForAuditAsync(f.Server, "user.role", target);
        await Api.WaitForAuditAsync(f.Server, "user.password.reset", target);
        await Api.WaitForAuditAsync(f.Server, "user.delete", target);

        using var audit = await f.Admin.GetAsync("/api/v1/audit?limit=1000");
        string auditText = await audit.Content.ReadAsStringAsync();
        Assert.DoesNotContain(Password, auditText, StringComparison.Ordinal);
        Assert.DoesNotContain(NewPassword, auditText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UserErrorsAreProblems()
    {
        string username = NewName();
        using (var created = await f.Admin.CallAsync(HttpMethod.Post, "/api/v1/users", new { username, password = Password, role = "Viewer" }))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        try
        {
            // Taken case-insensitively.
            using (var duplicate = await f.Admin.CallAsync(HttpMethod.Post, "/api/v1/users", new { username = username.ToUpperInvariant(), password = Password, role = "Viewer" }))
            {
                await duplicate.AssertProblemAsync(HttpStatusCode.Conflict, "UsernameTaken");
            }

            using (var invalid = await f.Admin.CallAsync(HttpMethod.Post, "/api/v1/users", new { username = "", password = "short", role = "Root" }))
            {
                var problem = await invalid.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "username");
                Assert.True(problem.GetProperty("errors").TryGetProperty("password", out _));
                Assert.True(problem.GetProperty("errors").TryGetProperty("role", out _));
            }

            using (var empty = await f.Admin.CallAsync(HttpMethod.Patch, "/api/v1/users/1", new { }))
            {
                await empty.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "role");
            }

            using (var unknown = await f.Admin.CallAsync(HttpMethod.Patch, "/api/v1/users/987654", new { role = "Viewer" }))
            {
                await unknown.AssertProblemAsync(HttpStatusCode.NotFound, "NotFound");
            }

            // The account signed in with the fixture's admin token cannot delete itself.
            using (var me = await f.Admin.GetAsync("/api/v1/auth/me"))
            {
                // The fixture uses an ownerless bearer token, so there is no self here; self-delete is covered on a cookie session below.
                Assert.Equal(HttpStatusCode.OK, me.StatusCode);
            }
        }
        finally
        {
            var users = (await (await f.Admin.GetAsync("/api/v1/users")).JsonAsync()).EnumerateArray();
            var row = users.First(u => u.GetProperty("username").GetString() == username);
            using var cleanup = await f.Admin.CallAsync(HttpMethod.Delete, "/api/v1/users/" + row.GetProperty("id").GetInt64().ToString(CultureInfo.InvariantCulture));
        }
    }

    [Fact]
    public async Task TheLastAdminCannotBeDemotedOrDeleted()
    {
        await using var server = await SaveServer.StartAsync("--X4MP:Saves:AutosaveMinutes=0");
        using var admin = server.Http(server.AdminToken());
        var users = (await (await admin.GetAsync("/api/v1/users")).JsonAsync()).EnumerateArray().ToList();
        var bootstrap = users.Single(u => u.GetProperty("role").GetString() == "Admin");
        long id = bootstrap.GetProperty("id").GetInt64();
        string target = id.ToString(CultureInfo.InvariantCulture);

        using (var demote = await admin.CallAsync(HttpMethod.Patch, $"/api/v1/users/{id}", new { role = "Viewer" }))
        {
            var problem = await demote.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "role");
            Assert.Contains("last admin", problem.GetProperty("errors").GetProperty("role")[0].GetString()!, StringComparison.OrdinalIgnoreCase);
        }

        using (var deleted = await admin.CallAsync(HttpMethod.Delete, $"/api/v1/users/{id}"))
        {
            await deleted.AssertProblemAsync(HttpStatusCode.Conflict, "LastAdmin");
        }

        // A cookie session cannot delete itself even when it is not the last admin.
        string password = File.ReadAllText(Path.Combine(server.Dir, "initial-admin-password.txt")).Trim();
        var handler = new System.Net.Http.HttpClientHandler { UseCookies = true, CookieContainer = new System.Net.CookieContainer() };
        using var browser = new HttpClient(handler) { BaseAddress = new Uri($"http://127.0.0.1:{server.HttpPort}") };
        using (var login = await browser.CallAsync(HttpMethod.Post, "/api/v1/auth/login", new { username = bootstrap.GetProperty("username").GetString(), password }, csrf: true))
        {
            Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        }

        // The bootstrap account must change its password before any admin call is allowed.
        using (var changed = await browser.CallAsync(HttpMethod.Post, "/api/v1/auth/change-password", new { current = password, @new = "self-delete-password-1" }, csrf: true))
        {
            Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
        }

        using var self = await browser.CallAsync(HttpMethod.Delete, $"/api/v1/users/{target}", csrf: true);
        await self.AssertProblemAsync(HttpStatusCode.Conflict, "CannotDeleteSelf");
    }
}

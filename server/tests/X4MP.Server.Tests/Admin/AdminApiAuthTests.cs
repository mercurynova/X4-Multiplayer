using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using X4MP.Persistence;
using X4MP.Server.Auth;
using X4MP.Server.Logging;

namespace X4MP.Server.Tests.Admin;

/// <summary>The auth routes under /api/v1, password change effects (acceptance 4, 5) and the secrets rule (acceptance 3).</summary>
public sealed class AdminApiAuthTests
{
    private const string AuthRoot = "/api/v1/auth";

    private static HttpClient Bearer(AuthFactory factory, string token)
    {
        var client = factory.NewClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<(long Id, string Token)> MintTokenAsync(HttpClient cookieClient, string name, string role = "Admin")
    {
        using var response = await cookieClient.SendAsync(AuthFactory.Post("/api/v1/tokens", new { name, role }));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.JsonAsync();
        return (body.GetProperty("id").GetInt64(), body.GetProperty("token").GetString()!);
    }

    [Fact]
    public async Task AuthRoutesLiveUnderApiV1AndTheOldPathIsGone()
    {
        await using var factory = new AuthFactory();
        var client = factory.NewClient();
        using (var old = await client.SendAsync(AuthFactory.Post("/api/auth/login", new { username = "admin", password = "x" })))
        {
            await old.AssertProblemAsync(HttpStatusCode.NotFound, "NotFound");
        }

        using (var bad = await client.SendAsync(AuthFactory.Post(AuthRoot + "/login", new { username = "admin", password = "wrong" })))
        {
            await bad.AssertProblemAsync(HttpStatusCode.Unauthorized, "InvalidCredentials");
        }

        using (var invalid = await client.SendAsync(AuthFactory.Post(AuthRoot + "/login", new { username = "", password = "" })))
        {
            var problem = await invalid.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "username");
            Assert.True(problem.GetProperty("errors").TryGetProperty("password", out _));
        }

        using (var me = await client.GetAsync(AuthRoot + "/me"))
        {
            await me.AssertProblemAsync(HttpStatusCode.Unauthorized, "Unauthorized");
        }

        using (var noCsrf = await client.SendAsync(AuthFactory.Post(AuthRoot + "/login", new { username = "admin", password = "x" }, csrf: false)))
        {
            await noCsrf.AssertProblemAsync(HttpStatusCode.BadRequest, "CsrfHeaderMissing");
        }

        var (admin, _) = await factory.AdminReadyAsync();
        using (var me = await admin.GetAsync(AuthRoot + "/me"))
        {
            Assert.Equal("admin", (await me.JsonAsync()).GetProperty("username").GetString());
        }

        using (var logout = await admin.SendAsync(AuthFactory.Post(AuthRoot + "/logout")))
        {
            Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        }
    }

    [Fact]
    public async Task ChangePasswordInvalidatesOtherSessionsAndOlderTokensButNotTheCurrentSession()
    {
        await using var factory = new AuthFactory();
        var (sessionA, password) = await factory.AdminReadyAsync();
        var sessionB = await factory.LoginAsync("admin", password);
        var (_, olderToken) = await MintTokenAsync(sessionA, "older");
        string ownerless = factory.Services.GetRequiredService<AdminStore>().CreateToken("script", AdminRoles.Admin);

        // everything works before the change
        using (var check = await sessionB.GetAsync(AuthRoot + "/me"))
        {
            Assert.Equal(HttpStatusCode.OK, check.StatusCode);
        }

        using (var bearer = Bearer(factory, olderToken))
        using (var check = await bearer.GetAsync("/api/v1/server"))
        {
            Assert.Equal(HttpStatusCode.OK, check.StatusCode);
        }

        const string newPassword = "an-entirely-different-passphrase";
        using (var change = await sessionA.SendAsync(AuthFactory.Post(AuthRoot + "/change-password", new { current = password, @new = newPassword })))
        {
            Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);
        }

        // the session that changed the password carries on
        using (var me = await sessionA.GetAsync(AuthRoot + "/me"))
        {
            Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        }

        using (var tokens = await sessionA.GetAsync("/api/v1/tokens"))
        {
            Assert.Equal(HttpStatusCode.OK, tokens.StatusCode);
        }

        // the other browser session is signed out
        using (var me = await sessionB.GetAsync(AuthRoot + "/me"))
        {
            await me.AssertProblemAsync(HttpStatusCode.Unauthorized, "Unauthorized");
        }

        using (var data = await sessionB.GetAsync("/api/v1/server"))
        {
            await data.AssertProblemAsync(HttpStatusCode.Unauthorized, "Unauthorized");
        }

        // the token minted before the change is revoked; one that belongs to nobody's password is not
        using (var bearer = Bearer(factory, olderToken))
        using (var check = await bearer.GetAsync("/api/v1/server"))
        {
            await check.AssertProblemAsync(HttpStatusCode.Unauthorized, "Unauthorized");
        }

        using (var bearer = Bearer(factory, ownerless))
        using (var check = await bearer.GetAsync("/api/v1/server"))
        {
            Assert.Equal(HttpStatusCode.OK, check.StatusCode);
        }

        // a token minted after the change works, and the old password no longer signs in
        var (_, newerToken) = await MintTokenAsync(sessionA, "newer");
        using (var bearer = Bearer(factory, newerToken))
        using (var check = await bearer.GetAsync("/api/v1/server"))
        {
            Assert.Equal(HttpStatusCode.OK, check.StatusCode);
        }

        using (var oldLogin = await factory.NewClient().SendAsync(AuthFactory.Post(AuthRoot + "/login", new { username = "admin", password })))
        {
            await oldLogin.AssertProblemAsync(HttpStatusCode.Unauthorized, "InvalidCredentials");
        }

        var again = await factory.LoginAsync("admin", newPassword);
        using (var me = await again.GetAsync(AuthRoot + "/me"))
        {
            Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        }
    }

    [Fact]
    public async Task ChangePasswordErrorsAreProblems()
    {
        await using var factory = new AuthFactory();
        var (admin, password) = await factory.AdminReadyAsync();
        using (var response = await admin.SendAsync(AuthFactory.Post(AuthRoot + "/change-password", new { current = "", @new = "" })))
        {
            var problem = await response.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "current");
            Assert.True(problem.GetProperty("errors").TryGetProperty("new", out _));
        }

        using (var response = await admin.SendAsync(AuthFactory.Post(AuthRoot + "/change-password", new { current = "not-the-password", @new = "another-long-passphrase" })))
        {
            await response.AssertProblemAsync(HttpStatusCode.BadRequest, "InvalidCurrentPassword");
        }

        using (var response = await admin.SendAsync(AuthFactory.Post(AuthRoot + "/change-password", new { current = password, @new = "short" })))
        {
            await response.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "new");
        }

        using (var response = await admin.SendAsync(AuthFactory.Post(AuthRoot + "/change-password", new { current = password, @new = password })))
        {
            await response.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "new");
        }
    }

    [Fact]
    public async Task NoPasswordOrTokenEverReachesTheLogsOrTheAuditLog()
    {
        await using var factory = new AuthFactory();
        string initial = factory.InitialPassword;
        var (admin, password) = await factory.AdminReadyAsync();
        string wrongGuess = "wrong-guess-" + Guid.NewGuid().ToString("N");
        using (var failed = await factory.NewClient().SendAsync(AuthFactory.Post(AuthRoot + "/login", new { username = "admin", password = wrongGuess })))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
        }

        var (id, token) = await MintTokenAsync(admin, "ci");
        using (var bearer = Bearer(factory, token))
        {
            using var read = await bearer.GetAsync("/api/v1/server");
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            using var settings = await bearer.SendAsync(AuthFactory.Post("/api/v1/chat", new { text = "hello" }));
            Assert.Equal(HttpStatusCode.Accepted, settings.StatusCode);
        }

        const string newPassword = "pass-phrase-never-to-be-logged-9";
        using (var change = await admin.SendAsync(AuthFactory.Post(AuthRoot + "/change-password", new { current = password, @new = newPassword })))
        {
            Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);
        }

        using (var revoke = await admin.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/tokens/{id}") { Headers = { { "X-X4MP", "1" } } }))
        {
            Assert.True(revoke.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound or HttpStatusCode.Conflict);
        }

        string[] secrets = [initial, password, wrongGuess, newPassword, token];

        // the audit log: actions and targets, never a secret
        string audit = string.Join('\n', factory.AuditRows().Select(r => $"{r.Actor}|{r.Action}|{r.Target}|{r.Data}"));
        Assert.Contains("auth.password.change", audit, StringComparison.Ordinal);
        Assert.Contains("token.create", audit, StringComparison.Ordinal);
        foreach (string secret in secrets)
        {
            Assert.DoesNotContain(secret, audit, StringComparison.Ordinal);
        }

        // the in-memory log buffer behind GET /logs and the rolling file under the data dir
        var ring = factory.Services.GetRequiredService<RingBufferSink>();
        string buffered = string.Join('\n', ring.Snapshot().Select(e => e.RenderMessage(System.Globalization.CultureInfo.InvariantCulture) + e.Exception));
        string files = string.Empty;
        foreach (string path in Directory.GetFiles(Path.Combine(factory.DataDir, "logs"), "*.log"))
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            files += await reader.ReadToEndAsync();
        }

        Assert.NotEmpty(buffered);
        Assert.NotEmpty(files);
        foreach (string secret in secrets)
        {
            Assert.DoesNotContain(secret, buffered, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, files, StringComparison.Ordinal);
        }

        // and the log endpoint does not hand them out either
        using var logs = await admin.GetAsync("/api/v1/logs?limit=1000");
        string logText = await logs.Content.ReadAsStringAsync();
        foreach (string secret in secrets)
        {
            Assert.DoesNotContain(secret, logText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task EveryProblemResponseHasTheSameShape()
    {
        await using var factory = new AuthFactory();
        var (admin, _) = await factory.AdminReadyAsync();
        using (var unauthenticated = await factory.NewClient().GetAsync("/api/v1/players"))
        {
            await unauthenticated.AssertProblemAsync(HttpStatusCode.Unauthorized, "Unauthorized");
        }

        using (var notFound = await admin.GetAsync("/api/v1/players/12345"))
        {
            await notFound.AssertProblemAsync(HttpStatusCode.NotFound, "NotFound");
        }

        using (var malformed = await admin.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/v1/bans")
        {
            Headers = { { "X-X4MP", "1" } },
            Content = new StringContent("{ nope", System.Text.Encoding.UTF8, "application/json"),
        }))
        {
            await malformed.AssertProblemAsync(HttpStatusCode.BadRequest, "InvalidRequest");
        }
    }
}

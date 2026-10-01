using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Events;
using X4MP.Persistence;
using X4MP.Server.Auth;
using X4MP.Server.Logging;

namespace X4MP.Server.Tests;

/// <summary>
/// Test host: temp data dir, no failure delay, remote IP taken from the <c>X-Test-Ip</c> header (TestServer has no
/// socket), and two probe endpoints guarded by the real Admin / Viewer policies.
/// </summary>
public sealed class AuthFactory(Dictionary<string, string>? settings = null) : WebApplicationFactory<Program>
{
    public const string IpHeader = "X-Test-Ip";
    public const string Csrf = "X-X4MP";

    public string DataDir { get; } = Path.Combine(Path.GetTempPath(), "x4mp-auth-" + Guid.NewGuid().ToString("N"));

    public string InitialPassword => ReadInitialPassword();

    private string ReadInitialPassword()
    {
        _ = Server; // make sure the host has started and bootstrapped
        return File.ReadAllText(Path.Combine(DataDir, "initial-admin-password.txt")).Trim();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        Directory.CreateDirectory(DataDir);
        builder.UseSetting("X4MP:DataDir", DataDir);
        builder.UseSetting("X4MP:Admin:FailureDelayMs", "0");
        foreach (var (key, value) in settings ?? [])
        {
            builder.UseSetting(key, value);
        }

        builder.UseContentRoot(DataDir);
        builder.ConfigureServices(services => services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter, TestStartupFilter>());
    }

    public HttpClient NewClient(string? ip = null)
    {
        var client = CreateClient();
        if (ip is not null)
        {
            client.DefaultRequestHeaders.Add(IpHeader, ip);
        }

        return client;
    }

    public static HttpRequestMessage Post(string url, object? body = null, bool csrf = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (csrf)
        {
            request.Headers.Add(Csrf, "1");
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    public async Task<HttpClient> LoginAsync(string username, string password, string? ip = null)
    {
        var client = NewClient(ip);
        using var response = await client.SendAsync(Post("/api/v1/auth/login", new { username, password }));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        return client;
    }

    /// <summary>Logs in as admin with the initial password and completes the forced change.</summary>
    public async Task<(HttpClient Client, string Password)> AdminReadyAsync()
    {
        var client = await LoginAsync("admin", InitialPassword);
        const string newPassword = "correct-horse-battery-staple";
        using var change = await client.SendAsync(Post("/api/v1/auth/change-password", new { current = InitialPassword, @new = newPassword }));
        Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);
        return (client, newPassword);
    }

    public List<(string Actor, string Action, string? Target, string? Data)> AuditRows()
    {
        using var db = new SqliteConnectionFactory(new PersistenceOptions { DataDir = DataDir }).Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT actor, action, target, data_json FROM audit_log ORDER BY id";
        using var reader = cmd.ExecuteReader();
        var rows = new List<(string, string, string?, string?)>();
        while (reader.Read())
        {
            rows.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return rows;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try
        {
            Directory.Delete(DataDir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class TestStartupFilter : Microsoft.AspNetCore.Hosting.IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                if (context.Request.Headers.TryGetValue(IpHeader, out var ip))
                {
                    context.Connection.RemoteIpAddress = IPAddress.Parse(ip.ToString());
                }

                await nextMiddleware(context);
            });

            app.UseWhen(c => c.Request.Path.StartsWithSegments("/api/test", StringComparison.Ordinal), branch =>
            {
                branch.UseRouting();
                branch.UseAuthentication();
                branch.UseAuthorization();
                branch.UseEndpoints(e =>
                {
                    e.MapGet("/api/test/admin", () => "ok").RequireAuthorization(AdminPolicies.Admin);
                    e.MapGet("/api/test/viewer", () => "ok").RequireAuthorization(AdminPolicies.Viewer);
                });
            });

            next(app);
        };
    }
}

public class AdminAuthTests
{
    private static readonly Dictionary<string, string> NoRateLimit = new() { ["X4MP:Admin:LoginMaxPerWindow"] = "1000" };

    [Fact]
    public async Task FirstRunCreatesAdminWithInitialPasswordFileAndHash()
    {
        await using var factory = new AuthFactory();
        _ = factory.NewClient();

        var password = factory.InitialPassword;
        Assert.Equal(20, password.Length);
        using var db = new SqliteConnectionFactory(new PersistenceOptions { DataDir = factory.DataDir }).Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT username, pw_iter, must_change, role, length(pw_hash) FROM admin_users";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("admin", reader.GetString(0));
        Assert.True(reader.GetInt32(1) >= 600_000);
        Assert.Equal(1, reader.GetInt32(2));
        Assert.Equal("Admin", reader.GetString(3));
        Assert.Equal(32, reader.GetInt32(4));
        Assert.Contains(factory.AuditRows(), r => r.Action == "auth.bootstrap");
    }

    [Fact]
    public async Task UnauthenticatedMeIs401AndLoginRejectsBadCredentials()
    {
        await using var factory = new AuthFactory();
        var client = factory.NewClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/auth/me")).StatusCode);

        using var wrongPassword = await client.SendAsync(AuthFactory.Post("/api/v1/auth/login", new { username = "admin", password = "nope" }));
        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        using var unknownUser = await client.SendAsync(AuthFactory.Post("/api/v1/auth/login", new { username = "ghost", password = "nope" }));
        Assert.Equal(HttpStatusCode.Unauthorized, unknownUser.StatusCode);
        using var empty = await client.SendAsync(AuthFactory.Post("/api/v1/auth/login", new { username = "", password = "" }));
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
    }

    [Fact]
    public async Task PostWithoutCsrfHeaderIs400WhileGetNeedsNone()
    {
        await using var factory = new AuthFactory();
        var client = factory.NewClient();

        using var response = await client.SendAsync(AuthFactory.Post("/api/v1/auth/login", new { username = "admin", password = factory.InitialPassword }, csrf: false));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("CsrfHeaderMissing", doc.RootElement.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    [Fact]
    public async Task LoginSetsStrictHttpOnlyCookieAndFirstPasswordChangeIsForced()
    {
        await using var factory = new AuthFactory();
        var client = factory.NewClient();
        var initial = factory.InitialPassword;

        using var login = await client.SendAsync(AuthFactory.Post("/api/v1/auth/login", new { username = "admin", password = initial }));
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        var cookie = Assert.Single(login.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith("x4mp_admin=", cookie, StringComparison.Ordinal);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);

        var me = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/me");
        Assert.Equal("admin", me.GetProperty("username").GetString());
        Assert.Equal("Admin", me.GetProperty("role").GetString());
        Assert.True(me.GetProperty("mustChangePassword").GetBoolean());

        // Forced: privileged endpoints are closed until the password is changed.
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/test/admin")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/test/viewer")).StatusCode);

        using var wrong = await client.SendAsync(AuthFactory.Post("/api/v1/auth/change-password", new { current = "wrong", @new = "a-long-enough-password" }));
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        using var weak = await client.SendAsync(AuthFactory.Post("/api/v1/auth/change-password", new { current = initial, @new = "short" }));
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
        using var same = await client.SendAsync(AuthFactory.Post("/api/v1/auth/change-password", new { current = initial, @new = initial }));
        Assert.Equal(HttpStatusCode.BadRequest, same.StatusCode);
        Assert.True(File.Exists(Path.Combine(factory.DataDir, "initial-admin-password.txt")));

        using var change = await client.SendAsync(AuthFactory.Post("/api/v1/auth/change-password", new { current = initial, @new = "a-long-enough-password" }));
        Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);
        Assert.False(File.Exists(Path.Combine(factory.DataDir, "initial-admin-password.txt")));

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/test/admin")).StatusCode);
        var after = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/me");
        Assert.False(after.GetProperty("mustChangePassword").GetBoolean());

        // Old password is dead, new one works from a fresh client.
        using var fresh = factory.NewClient();
        using var oldLogin = await fresh.SendAsync(AuthFactory.Post("/api/v1/auth/login", new { username = "admin", password = initial }));
        Assert.Equal(HttpStatusCode.Unauthorized, oldLogin.StatusCode);
        using var newLogin = await fresh.SendAsync(AuthFactory.Post("/api/v1/auth/login", new { username = "admin", password = "a-long-enough-password" }));
        Assert.Equal(HttpStatusCode.NoContent, newLogin.StatusCode);
    }

    [Fact]
    public async Task LogoutEndsTheSession()
    {
        await using var factory = new AuthFactory();
        var (client, _) = await factory.AdminReadyAsync();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/auth/me")).StatusCode);

        using var logout = await client.SendAsync(AuthFactory.Post("/api/v1/auth/logout"));
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/test/admin")).StatusCode);
    }

    [Fact]
    public async Task SixthLoginWithinAMinuteIs429WithRetryAfter()
    {
        await using var factory = new AuthFactory();
        var client = factory.NewClient();

        for (var i = 0; i < 5; i++)
        {
            using var attempt = await client.SendAsync(AuthFactory.Post("/api/v1/auth/login", new { username = "admin", password = "bad" + i }));
            Assert.Equal(HttpStatusCode.Unauthorized, attempt.StatusCode);
        }

        using var sixth = await client.SendAsync(AuthFactory.Post("/api/v1/auth/login", new { username = "admin", password = factory.InitialPassword }));
        Assert.Equal(HttpStatusCode.TooManyRequests, sixth.StatusCode);
        Assert.True(sixth.Headers.Contains("Retry-After"));

        // A different client IP has its own window.
        using var other = factory.NewClient("192.168.1.20");
        using var ok = await other.SendAsync(AuthFactory.Post("/api/v1/auth/login", new { username = "admin", password = factory.InitialPassword }));
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);
    }

    [Fact]
    public async Task AccountLocksAfterRepeatedFailuresEvenWithTheRightPassword()
    {
        var settings = new Dictionary<string, string>(NoRateLimit) { ["X4MP:Admin:LockoutFailures"] = "3" };
        await using var factory = new AuthFactory(settings);
        var client = factory.NewClient();

        for (var i = 0; i < 3; i++)
        {
            using var attempt = await client.SendAsync(AuthFactory.Post("/api/v1/auth/login", new { username = "admin", password = "bad" + i }));
            Assert.Equal(HttpStatusCode.Unauthorized, attempt.StatusCode);
        }

        using var locked = await client.SendAsync(AuthFactory.Post("/api/v1/auth/login", new { username = "admin", password = factory.InitialPassword }));
        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
        Assert.Contains(factory.AuditRows(), r => r.Action == "auth.login.locked");
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("2001:4860:4860::8888")]
    public async Task PublicIpIs403ByDefault(string ip)
    {
        await using var factory = new AuthFactory(NoRateLimit);
        var client = factory.NewClient(ip);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/healthz")).StatusCode);
        using var login = await client.SendAsync(AuthFactory.Post("/api/v1/auth/login", new { username = "admin", password = factory.InitialPassword }));
        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.20.0.9")]
    [InlineData("192.168.50.7")]
    [InlineData("::ffff:192.168.1.5")]
    [InlineData("fd12:3456::1")]
    public async Task LoopbackAndPrivateIpsAreAllowed(string ip)
    {
        await using var factory = new AuthFactory();
        Assert.Equal(HttpStatusCode.OK, (await factory.NewClient(ip).GetAsync("/healthz")).StatusCode);
    }

    [Fact]
    public async Task ConfiguredNetworkOpensPublicRangeAndPrivateCanBeDisabledButNotLoopback()
    {
        await using var allowed = new AuthFactory(new() { ["X4MP:Admin:AllowedNetworks:0"] = "8.8.8.0/24" });
        Assert.Equal(HttpStatusCode.OK, (await allowed.NewClient("8.8.8.8").GetAsync("/healthz")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await allowed.NewClient("9.9.9.9").GetAsync("/healthz")).StatusCode);

        await using var strict = new AuthFactory(new() { ["X4MP:Admin:AllowPrivateNetworks"] = "false" });
        Assert.Equal(HttpStatusCode.Forbidden, (await strict.NewClient("192.168.1.5").GetAsync("/healthz")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await strict.NewClient("127.0.0.1").GetAsync("/healthz")).StatusCode);
    }

    [Fact]
    public async Task SecurityHeadersAreOnEveryResponse()
    {
        await using var factory = new AuthFactory();
        var client = factory.NewClient();
        foreach (var path in new[] { "/healthz", "/", "/api/v1/auth/me", "/nothing-here.js" })
        {
            var response = await client.GetAsync(path);
            Assert.Contains("default-src 'self'", string.Join(';', response.Headers.GetValues("Content-Security-Policy")), StringComparison.Ordinal);
            Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
            Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        }
    }

    [Fact]
    public async Task BearerTokensAuthenticateWithTheirRoleAndAreStoredHashed()
    {
        await using var factory = new AuthFactory();
        _ = factory.NewClient();
        var store = factory.Services.GetRequiredService<AdminStore>();
        var viewer = store.CreateToken("ci-viewer", AdminRoles.Viewer);
        var admin = store.CreateToken("ci-admin", AdminRoles.Admin);
        Assert.StartsWith("x4mp_", viewer, StringComparison.Ordinal);

        var client = factory.NewClient();
        async Task<HttpStatusCode> Get(string path, string? token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            if (token is not null)
            {
                request.Headers.Authorization = new("Bearer", token);
            }

            using var response = await client.SendAsync(request);
            return response.StatusCode;
        }

        Assert.Equal(HttpStatusCode.Unauthorized, await Get("/api/test/viewer", null));
        Assert.Equal(HttpStatusCode.Unauthorized, await Get("/api/test/viewer", "x4mp_bogus"));
        Assert.Equal(HttpStatusCode.OK, await Get("/api/test/viewer", viewer));
        Assert.Equal(HttpStatusCode.Forbidden, await Get("/api/test/admin", viewer));
        Assert.Equal(HttpStatusCode.OK, await Get("/api/test/admin", admin));
        Assert.Equal(HttpStatusCode.OK, await Get("/api/v1/auth/me", admin));

        // Bearer requests are exempt from the CSRF header (no ambient credentials) and cannot change passwords.
        using var post = AuthFactory.Post("/api/v1/auth/change-password", new { current = "a", @new = "b" }, csrf: false);
        post.Headers.Authorization = new("Bearer", admin);
        using var postResponse = await client.SendAsync(post);
        Assert.NotEqual(HttpStatusCode.BadRequest, postResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, postResponse.StatusCode);

        using var db = new SqliteConnectionFactory(new PersistenceOptions { DataDir = factory.DataDir }).Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM api_tokens WHERE token_hash = $h AND length(token_hash) = 32";
        cmd.Parameters.AddWithValue("$h", AdminStore.HashToken(viewer));
        Assert.Equal(1L, Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task AuthActionsAreAudited()
    {
        await using var factory = new AuthFactory(NoRateLimit);
        var client = factory.NewClient();
        var initial = factory.InitialPassword;

        (await client.SendAsync(AuthFactory.Post("/api/v1/auth/login", new { username = "admin", password = "wrong" }))).Dispose();
        (await client.SendAsync(AuthFactory.Post("/api/v1/auth/login", new { username = "admin", password = initial }))).Dispose();
        (await client.SendAsync(AuthFactory.Post("/api/v1/auth/change-password", new { current = "wrong", @new = "a-long-enough-password" }))).Dispose();
        (await client.SendAsync(AuthFactory.Post("/api/v1/auth/change-password", new { current = initial, @new = "a-long-enough-password" }))).Dispose();
        (await client.SendAsync(AuthFactory.Post("/api/v1/auth/logout"))).Dispose();

        var actions = factory.AuditRows().Select(r => r.Action).ToList();
        Assert.Equal(
            ["auth.bootstrap", "auth.login.failure", "auth.login.success", "auth.password.failure", "auth.password.change", "auth.logout"],
            actions);
        var failure = factory.AuditRows().First(r => r.Action == "auth.login.failure");
        Assert.Equal("admin", failure.Actor);
        Assert.DoesNotContain("wrong", failure.Data ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task PasswordsAndTokensNeverAppearInLogsOrAudit()
    {
        await using var factory = new AuthFactory(NoRateLimit);
        var ring = factory.Services.GetRequiredService<RingBufferSink>();
        var client = factory.NewClient();
        var initial = factory.InitialPassword;
        const string wrong = "Wr0ng-Secret-Value!";
        const string newPassword = "N3w-Secret-Passphrase!";

        (await client.SendAsync(AuthFactory.Post("/api/v1/auth/login", new { username = "admin", password = wrong }))).Dispose();
        (await client.SendAsync(AuthFactory.Post("/api/v1/auth/login", new { username = "admin", password = initial }))).Dispose();
        (await client.SendAsync(AuthFactory.Post("/api/v1/auth/change-password", new { current = initial, @new = newPassword }))).Dispose();
        var token = factory.Services.GetRequiredService<AdminStore>().CreateToken("t", AdminRoles.Viewer);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/test/viewer");
        request.Headers.Authorization = new("Bearer", token);
        (await client.SendAsync(request)).Dispose();

        var logged = string.Join(
            "\n",
            ring.Snapshot().Select(e => e.RenderMessage() + " " + string.Join(" ", e.Properties.Select(p => p.Value.ToString()))
                + (e.Exception?.ToString() ?? "")));
        Assert.True(ring.Count > 0);
        foreach (var secret in new[] { wrong, initial, newPassword, token })
        {
            Assert.DoesNotContain(secret, logged, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, string.Join("\n", factory.AuditRows().Select(r => r.Data + r.Target + r.Actor)), StringComparison.Ordinal);
        }

        Assert.DoesNotContain(ring.Snapshot(), e => e.Level >= LogEventLevel.Error);
    }
}

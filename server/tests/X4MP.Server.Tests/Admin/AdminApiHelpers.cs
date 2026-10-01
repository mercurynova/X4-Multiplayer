using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using X4MP.FakeNode;
using X4MP.Persistence;
using X4MP.Proto;
using X4MP.Protocol;
using X4MP.Protocol.Client;
using X4MP.Server.Tests.Saves;

namespace X4MP.Server.Tests.Admin;

/// <summary>Request and response helpers of the admin API tests.</summary>
internal static class Api
{
    public static async Task<HttpResponseMessage> CallAsync(this HttpClient client, HttpMethod method, string url, object? body = null, bool csrf = false)
    {
        using var request = new HttpRequestMessage(method, url);
        if (csrf)
        {
            request.Headers.Add("X-X4MP", "1");
        }

        if (body is not null)
        {
            request.Content = body is string raw
                ? new StringContent(raw, System.Text.Encoding.UTF8, "application/json")
                : JsonContent.Create(body);
        }

        return await client.SendAsync(request);
    }

    public static Task<HttpResponseMessage> PostJsonAsync(this HttpClient client, string url, object? body = null) => client.CallAsync(HttpMethod.Post, url, body);

    public static async Task<JsonElement> JsonAsync(this HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    /// <summary>Asserts an RFC 7807 problem: media type, <c>type</c>/<c>title</c>/<c>status</c>/<c>code</c>, and (optionally) an <c>errors</c> entry for a key.</summary>
    public static async Task<JsonElement> AssertProblemAsync(this HttpResponseMessage response, HttpStatusCode status, string code, string? errorKey = null)
    {
        string text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"expected {(int)status} {code}, got {(int)response.StatusCode}: {text}");
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var root = JsonDocument.Parse(text).RootElement.Clone();
        Assert.Equal((int)status, root.GetProperty("status").GetInt32());
        Assert.Equal(code, root.GetProperty("code").GetString());
        Assert.Equal("urn:x4mp:problem:" + code, root.GetProperty("type").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("title").GetString()));
        if (errorKey is not null)
        {
            Assert.True(root.GetProperty("errors").TryGetProperty(errorKey, out var messages), $"errors has no '{errorKey}': {text}");
            Assert.True(messages.GetArrayLength() > 0);
        }

        return root;
    }

    /// <summary>
    /// Asserts the server dropped the node within <paramref name="timeout"/>: it saw the <c>Disconnect</c> with the expected code, or the
    /// socket was closed or reset (a reset can swallow the frame the server sent just before closing).
    /// </summary>
    public static async Task AssertDisconnectedAsync(TcpNodeClient client, DisconnectCode expected, TimeSpan timeout)
    {
        var code = await WaitForDisconnectAsync(client, timeout);
        Assert.NotNull(code); // null = still connected after the timeout
        Assert.True(code == expected || code == DisconnectCode.None, $"expected {expected}, saw {code}");
    }

    private static async Task<DisconnectCode?> WaitForDisconnectAsync(TcpNodeClient client, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            while (true)
            {
                var frame = await client.ReceiveAsync(cts.Token);
                if (frame is null)
                {
                    return DisconnectCode.None;
                }

                if (frame.Value.Type == MsgType.Disconnect)
                {
                    return MessageRegistry.Default.Decode<Disconnect>(frame.Value).Code;
                }
            }
        }
        catch (OperationCanceledException)
        {
            return null; // still connected
        }
        catch (IOException)
        {
            return DisconnectCode.None;
        }
    }

    /// <summary>Polls the audit log until a row with <paramref name="action"/> (and the target, if given) exists.</summary>
    public static async Task<AuditRecord> WaitForAuditAsync(SaveServer server, string action, string? target = null)
    {
        var queries = server.Service<SqliteAdminQueries>();
        AuditRecord? found = null;
        await SaveServer.WaitUntilAsync(
            () => (found = queries.AuditEntries(1000).FirstOrDefault(a => a.Action == action && (target is null || a.Target == target))) is not null,
            10_000,
            "audit row " + action);
        return found!;
    }
}

/// <summary>A full server with an Admin and a Viewer token, shared by the tests of a class.</summary>
public sealed class AdminServerFixture : IAsyncLifetime
{
    public SaveServer Server { get; private set; } = null!;

    public HttpClient Admin { get; private set; } = null!;

    public HttpClient Viewer { get; private set; } = null!;

    /// <summary>No credentials (it sends the CSRF header, so the request reaches the authentication layer).</summary>
    public HttpClient Anon { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Server = await SaveServer.StartAsync("--X4MP:Saves:AutosaveMinutes=0");
        Admin = Server.Http(Server.AdminToken());
        Viewer = Server.Http(Server.AdminToken("Viewer"));
        Anon = Server.Http();
        Anon.DefaultRequestHeaders.Add("X-X4MP", "1");
    }

    public async Task DisposeAsync()
    {
        Admin.Dispose();
        Viewer.Dispose();
        Anon.Dispose();
        await Server.DisposeAsync();
    }

    private int _names;

    public string NextName(string prefix) => prefix + Interlocked.Increment(ref _names).ToString("00", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Connects a client node and waits until the session lists it as connected; returns its persistent player id.</summary>
    public async Task<(TcpNodeClient Node, long PlayerId)> JoinAsync(string name)
    {
        var node = await Server.ConnectAsync(name, Role.Client);
        await Server.WaitForAsync(s => s.Nodes.Any(n => n.Name == name && n.Connected), 10_000, "join " + name);
        return (node, node.Welcome.PlayerId);
    }

    /// <summary>Makes a player row without a connection (an offline player).</summary>
    public async Task<long> CreateOfflinePlayerAsync(string name)
    {
        var store = Server.Service<X4MP.Core.Session.IPlayerStore>();
        var key = LiveRunner.DeriveKey(7, name);
        var bound = await store.BindAsync(name, System.Security.Cryptography.SHA256.HashData(key), System.Net.IPAddress.Parse("10.9.9.9"), DateTimeOffset.UtcNow, CancellationToken.None);
        return bound.PlayerId;
    }
}

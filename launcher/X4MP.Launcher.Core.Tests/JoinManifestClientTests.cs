using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace X4MP.Launcher.Core.Tests;

public class JoinManifestClientTests
{
    private const string Password = "correct horse battery staple";
    private const string NonceHex = "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff";

    [Fact]
    public async Task FetchesTheManifestFromAnOpenServer()
    {
        using var handler = new StubHandler()
            .Add(Json("""{"nonce":"00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff","passwordRequired":false,"expiresInSeconds":120}"""))
            .Add(Json("""
                {"session":"Friday Run","policyVersion":7,"gameBuild":"900-611726","unknownDefault":"AllowClientOnly",
                 "mods":[{"id":"sim_mod","name":"Sim Mod","rule":"Required","enabled":true,"class":"Sim","version":"1.30",
                          "workshopId":42,"nexusUrl":null,"notes":""}]}
                """));
        using var http = new HttpClient(handler);

        var manifest = await new JoinManifestClient(http).FetchAsync("localhost:47790", null, CancellationToken.None);

        Assert.Equal("Friday Run", manifest.Session);
        Assert.Equal(7, manifest.PolicyVersion);
        Assert.Equal("900-611726", manifest.GameBuild);
        Assert.Equal("AllowClientOnly", manifest.UnknownDefault);
        var mod = Assert.Single(manifest.Mods);
        Assert.Equal("sim_mod", mod.Id);
        Assert.Equal(42, mod.WorkshopId);
        Assert.Null(mod.NexusUrl);

        Assert.Equal("http://localhost:47790/api/v1/join/nonce", handler.Requests[0].Uri!.ToString());
        Assert.Equal("http://localhost:47790/api/v1/join/mod-manifest", handler.Requests[1].Uri!.ToString());
        Assert.DoesNotContain("X-X4MP-Proof", handler.Requests[1].Headers.Keys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AJoinPasswordIsProvedWithTheWireConstruction()
    {
        using var handler = new StubHandler()
            .Add(Json($$"""{"nonce":"{{NonceHex}}","passwordRequired":true,"expiresInSeconds":120}"""))
            .Add(Json("""{"session":"Friday Run","policyVersion":7,"gameBuild":"900-611726","unknownDefault":"AllowClientOnly","mods":[]}"""));
        using var http = new HttpClient(handler);

        var manifest = await new JoinManifestClient(http).FetchAsync("http://127.0.0.1:47790", Password, CancellationToken.None);

        Assert.Equal("Friday Run", manifest.Session);
        var request = handler.Requests[1];
        Assert.Equal(NonceHex, request.Headers["X-X4MP-Nonce"]);

        var playerKey = Convert.FromHexString(request.Headers["X-X4MP-Player-Key"]);
        Assert.Equal(32, playerKey.Length);

        var passwordHash = SHA256.HashData(Encoding.UTF8.GetBytes(Password));
        byte[] message = [.. Convert.FromHexString(NonceHex), .. playerKey];
        var expected = Convert.ToHexStringLower(HMACSHA256.HashData(passwordHash, message));
        Assert.Equal(expected, request.Headers["X-X4MP-Proof"]);

        Assert.DoesNotContain(Password, request.Uri!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task APasswordServerNeedsThePasswordBeforeTheManifestIsAskedFor()
    {
        using var handler = new StubHandler()
            .Add(Json($$"""{"nonce":"{{NonceHex}}","passwordRequired":true,"expiresInSeconds":120}"""));
        using var http = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<JoinClientException>(
            () => new JoinManifestClient(http).FetchAsync("http://127.0.0.1:47790", null, CancellationToken.None));

        Assert.Equal("JoinAuthRequired", error.Code);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "JoinAuthFailed", "not accepted")]
    [InlineData(HttpStatusCode.Unauthorized, "JoinNonceExpired", "took too long")]
    [InlineData(HttpStatusCode.Forbidden, "NetworkNotAllowed", "network")]
    [InlineData(HttpStatusCode.NotFound, "NotFound", "older build")]
    public async Task ServerErrorsBecomeMessagesAPlayerCanActOn(HttpStatusCode status, string code, string fragment)
    {
        using var handler = new StubHandler()
            .Add(Json($$"""{"nonce":"{{NonceHex}}","passwordRequired":true,"expiresInSeconds":120}"""))
            .Add(Problem(status, code));
        using var http = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<JoinClientException>(
            () => new JoinManifestClient(http).FetchAsync("http://127.0.0.1:47790", Password, CancellationToken.None));

        Assert.Equal(code, error.Code);
        Assert.Contains(fragment, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task APageThatIsNotJsonIsReportedAsABadResponse()
    {
        using var handler = new StubHandler()
            .Add(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html><body>hello</body></html>", Encoding.UTF8, "text/html"),
            });
        using var http = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<JoinClientException>(
            () => new JoinManifestClient(http).FetchAsync("127.0.0.1:47790", null, CancellationToken.None));

        Assert.Equal("BadResponse", error.Code);
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Problem(HttpStatusCode status, string code) =>
        new(status)
        {
            Content = new StringContent(
                $$"""{"type":"urn:x4mp:problem:{{code}}","title":"Problem","status":{{(int)status}},"code":"{{code}}","detail":"no"}""",
                Encoding.UTF8,
                "application/problem+json"),
        };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _responses = new();

        public List<RecordedRequest> Requests { get; } = [];

        public StubHandler Add(HttpResponseMessage response)
        {
            _responses.Enqueue(() => response);
            return this;
        }

        public StubHandler Add(Func<HttpResponseMessage> response)
        {
            _responses.Enqueue(response);
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in request.Headers) headers[header.Key] = string.Join(",", header.Value);
            Requests.Add(new RecordedRequest(request.Method, request.RequestUri, headers));
            if (_responses.Count == 0) throw new InvalidOperationException($"No response queued for {request.RequestUri}");
            return Task.FromResult(_responses.Dequeue()());
        }
    }

    private sealed record RecordedRequest(HttpMethod Method, Uri? Uri, IReadOnlyDictionary<string, string> Headers);
}

using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.FileProviders;
using X4MP.Persistence;

namespace X4MP.Server.Tests;

/// <summary>Spins the real server in-process (TestServer) against a temp data dir.</summary>
public sealed class ServerFactory : WebApplicationFactory<Program>
{
    public string DataDir { get; } = Path.Combine(Path.GetTempPath(), "x4mp-test-" + Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseSetting("X4MP:DataDir", DataDir);
        // A content root with no wwwroot/dist: everything the GUI needs must come from the embedded provider.
        Directory.CreateDirectory(DataDir);
        builder.UseContentRoot(DataDir);
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
            // Serilog keeps the log file open until process exit; leftover temp files are harmless.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

public class ServerHostTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task HealthzReturnsStatusVersionProtocolAndUptime()
    {
        var response = await _client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal("ok", root.GetProperty("status").GetString());
        Assert.False(string.IsNullOrEmpty(root.GetProperty("version").GetString()));
        var protocol = root.GetProperty("protocol");
        Assert.True(protocol.GetProperty("min").GetInt32() <= protocol.GetProperty("max").GetInt32());
        Assert.True(root.GetProperty("uptimeSeconds").GetInt64() >= 0);
    }

    [Fact]
    public async Task DeepLinkReturnsIndexHtmlWithNoCache()
    {
        var root = await _client.GetAsync("/");
        var deep = await _client.GetAsync("/players");
        Assert.Equal(HttpStatusCode.OK, deep.StatusCode);
        Assert.Equal("text/html", deep.Content.Headers.ContentType?.MediaType);
        Assert.Contains("no-cache", deep.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
        Assert.Equal(await root.Content.ReadAsStringAsync(), await deep.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/api/v1/nothing")]
    [InlineData("/hubs/nothing")] // /hubs/admin itself is the SignalR hub since M1-S3
    [InlineData("/files/x")]
    [InlineData("/assets/does-not-exist")]
    public async Task ReservedAndMissingAssetPathsAreNotTheSpa(string path)
    {
        var response = await _client.GetAsync(new Uri(path, UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task TheAdminHubRouteIsTheHubNotTheSpa()
    {
        var response = await _client.PostAsync(new Uri("/hubs/admin/negotiate?negotiateVersion=1", UriKind.Relative), content: null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode); // anonymous: refused by the hub's authorization, never index.html
        Assert.NotEqual("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task NonGetOnSpaRouteIsNotIndexHtml()
    {
        var response = await _client.PostAsync(new Uri("/players", UriKind.Relative), content: null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task EmbeddedAssetsAreServedImmutableWithoutAnyFilesOnDisk()
    {
        // Content root is an empty temp dir and server/web/dist is never consulted: bytes come from the assembly.
        var provider = new ManifestEmbeddedFileProvider(typeof(Program).Assembly, "wwwroot");
        Assert.True(provider.GetFileInfo("index.html").Exists, "index.html must be embedded");

        var assets = provider.GetDirectoryContents("assets").ToList();
        if (assets.Count == 0)
        {
            return; // GUI not built (SkipWebBuild without dist): only the placeholder page is embedded.
        }

        foreach (var asset in assets)
        {
            var response = await _client.GetAsync(new Uri("/assets/" + asset.Name, UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("public, max-age=31536000, immutable", response.Headers.CacheControl?.ToString());
            await using var expected = asset.CreateReadStream();
            using var memory = new MemoryStream();
            await expected.CopyToAsync(memory);
            Assert.Equal(memory.ToArray(), await response.Content.ReadAsByteArrayAsync());
        }
    }

    [Fact]
    public async Task GracefulStopLogsShutdownComplete()
    {
        var shutdownFactory = new ServerFactory();
        var ring = shutdownFactory.Services.GetService(typeof(X4MP.Server.Logging.RingBufferSink)) as X4MP.Server.Logging.RingBufferSink;
        Assert.NotNull(ring);

        await shutdownFactory.DisposeAsync();

        Assert.Contains(ring.Snapshot(), e => e.RenderMessage() == "shutdown complete");
    }

    [Fact]
    public void StartupMigratedTheDatabaseIntoTheDataDir()
    {
        _ = _client; // ensure the host started
        var options = new PersistenceOptions { DataDir = factory.DataDir };
        Assert.True(File.Exists(options.DatabasePath));
        Assert.True(Directory.Exists(Path.Combine(factory.DataDir, "logs")));
        using var connection = new SqliteConnectionFactory(options).Open();
        Assert.True(MigrationRunner.GetCurrentVersion(connection) >= 1);
    }
}

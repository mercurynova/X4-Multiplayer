using System.Net;
using System.Security.Cryptography.X509Certificates;
using X4MP.Server.Hosting;
using X4MP.Server.Tests.Saves;

namespace X4MP.Server.Tests.Admin;

/// <summary>HTTPS on the admin port (server-design 4.3): a supplied PFX or a generated self-signed certificate.</summary>
public sealed class AdminHttpsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "x4mp-cert-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void NoCertificateAndNoGenerateMeansPlainHttp()
    {
        Assert.Null(AdminCertificates.Resolve(_dir, null, null, generate: false));
        Assert.False(Directory.Exists(Path.Combine(_dir, "certs")));
    }

    [Fact]
    public void TheGeneratedCertificateIsWrittenOnceAndReused()
    {
        var first = AdminCertificates.EnsureSelfSigned(_dir);
        string file = Path.Combine(_dir, "certs", AdminCertificates.FileName);
        Assert.True(File.Exists(file));
        Assert.True(first.HasPrivateKey);
        Assert.True(first.NotAfter > DateTime.UtcNow.AddYears(4));
        Assert.Contains("localhost", San(first), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("127.0.0.1", San(first), StringComparison.Ordinal);

        string fingerprint = AdminCertificates.Fingerprint(first);
        Assert.Matches("^([0-9A-F]{2}:){31}[0-9A-F]{2}$", fingerprint);

        // A restart finds the file usable and keeps the same certificate, so a trusted fingerprint stays valid.
        using var second = AdminCertificates.EnsureSelfSigned(_dir);
        Assert.Equal(fingerprint, AdminCertificates.Fingerprint(second));

        // An expired (or nearly expired) file is replaced, not served.
        var time = new FakeTime(DateTimeOffset.UtcNow.AddYears(6));
        using var renewed = AdminCertificates.EnsureSelfSigned(_dir, time: time);
        Assert.NotEqual(fingerprint, AdminCertificates.Fingerprint(renewed));
        Assert.True(renewed.NotAfter > time.GetUtcNow().UtcDateTime.AddYears(4));
    }

    [Fact]
    public void AConfiguredPfxIsUsedAsIs()
    {
        using var made = AdminCertificates.EnsureSelfSigned(_dir);
        string file = Path.Combine(_dir, "certs", AdminCertificates.FileName);
        using var loaded = AdminCertificates.Resolve(_dir, file, null, generate: false);
        Assert.NotNull(loaded);
        Assert.Equal(AdminCertificates.Fingerprint(made), AdminCertificates.Fingerprint(loaded));
    }

    [Fact]
    public async Task TheAdminPortAnswersHttpsWhenAGeneratedCertificateIsAskedFor()
    {
        int httpsPort = TestPorts.FreeTcp();
        await using var server = await SaveServer.StartAsync(
            "--X4MP:Admin:HttpsPort=" + httpsPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--X4MP:Admin:GenerateSelfSigned=true");

        Assert.True(File.Exists(Path.Combine(server.Dir, "certs", AdminCertificates.FileName)));

        X509Certificate2? seen = null;
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
            {
                seen = certificate is null ? null : new X509Certificate2(certificate);
                return true; // the default test trust store does not hold the generated certificate
            },
        };
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://127.0.0.1:" + httpsPort.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            Timeout = TimeSpan.FromMinutes(2),
        };

        using (var health = await client.GetAsync("/healthz"))
        {
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        }

        Assert.NotNull(seen);
        Assert.Contains("localhost", San(seen!), StringComparison.OrdinalIgnoreCase);

        using var admin = server.Http(server.AdminToken());
        using var info = await admin.GetAsync("/api/v1/server");
        var body = await info.JsonAsync();
        Assert.True(body.GetProperty("https").GetBoolean());
        string fingerprint = body.GetProperty("certificateFingerprint").GetString()!;
        Assert.Equal(AdminCertificates.Fingerprint(seen!), fingerprint);

        // Plain HTTP keeps working beside it (LAN tools that have not been pointed at HTTPS yet).
        using var plain = await server.Http().GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, plain.StatusCode);
    }

    [Fact]
    public async Task HttpsPortWithoutACertificateStaysOnPlainHttp()
    {
        await using var server = await SaveServer.StartAsync(
            "--X4MP:Admin:HttpsPort=" + TestPorts.FreeTcp().ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var admin = server.Http(server.AdminToken());
        using var info = await admin.GetAsync("/api/v1/server");
        var body = await info.JsonAsync();
        Assert.False(body.GetProperty("https").GetBoolean());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, body.GetProperty("certificateFingerprint").ValueKind);
    }

    private static string San(X509Certificate2 certificate) =>
        certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault()?.Format(false) ?? "";

    private sealed class FakeTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

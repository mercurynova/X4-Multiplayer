using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace X4MP.Server.Hosting;

/// <summary>
/// Resolves the certificate the admin port serves HTTPS with (server-design 4.3): a PFX the admin supplied, or a self-signed
/// one created once in <c>data/certs/admin.pfx</c>. The certificate is never regenerated while the stored file loads and is
/// still valid, so browsers that were told to trust the fingerprint keep working.
/// </summary>
public static class AdminCertificates
{
    /// <summary>File name inside <c>data/certs</c> of the generated certificate.</summary>
    public const string FileName = "admin.pfx";

    /// <summary>How long a generated certificate is valid for.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(365 * 5);

    /// <summary>Loads the configured PFX, or creates/loads the self-signed one. Null when neither was asked for.</summary>
    public static X509Certificate2? Resolve(string dataDir, string? path, string? password, bool generate)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            return X509CertificateLoader.LoadPkcs12FromFile(path, password);
        }

        return generate ? EnsureSelfSigned(dataDir) : null;
    }

    /// <summary>The self-signed certificate in <paramref name="dataDir"/>, created on first use and reloaded afterwards.</summary>
    public static X509Certificate2 EnsureSelfSigned(string dataDir, string? commonName = null, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDir);
        string dir = Path.Combine(dataDir, "certs");
        string file = Path.Combine(dir, FileName);
        if (File.Exists(file) && TryLoad(file) is { } existing && IsUsable(existing, time ?? TimeProvider.System))
        {
            return existing;
        }

        Directory.CreateDirectory(dir);
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=" + (commonName ?? "X4MP admin"), rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        var eku = new OidCollection { new("1.3.6.1.5.5.7.3.1") }; // serverAuth
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(eku, false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        san.AddIpAddress(IPAddress.IPv6Loopback);
        try
        {
            san.AddDnsName(Environment.MachineName);
        }
        catch (InvalidOperationException)
        {
            // A host name the SAN builder refuses (unusual characters) is not worth failing the start over.
        }

        foreach (var address in LocalAddresses())
        {
            san.AddIpAddress(address);
        }

        request.CertificateExtensions.Add(san.Build());

        var now = (time ?? TimeProvider.System).GetUtcNow();
        using var created = request.CreateSelfSigned(now.AddDays(-1), now.Add(Lifetime));
        byte[] pfx = created.Export(X509ContentType.Pfx);
        var scratch = file + ".tmp";
        File.WriteAllBytes(scratch, pfx);
        File.Move(scratch, file, overwrite: true);

        return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.Exportable);
    }

    /// <summary>The certificate's SHA-256 thumbprint in the usual <c>AA:BB:...</c> form (what the UI shows for verification).</summary>
    public static string Fingerprint(X509Certificate2 certificate) =>
        string.Join(':', certificate.GetCertHash(HashAlgorithmName.SHA256).Select(b => b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)));

    /// <summary>A generated certificate is replaced when it expires within 30 days (clock skew and renewal margin).</summary>
    private static bool IsUsable(X509Certificate2 certificate, TimeProvider time)
    {
        var now = time.GetUtcNow().UtcDateTime;
        return certificate.NotBefore <= now.AddDays(1) && certificate.NotAfter > now.AddDays(30) && certificate.HasPrivateKey;
    }

    private static X509Certificate2? TryLoad(string file)
    {
        try
        {
            return X509CertificateLoader.LoadPkcs12FromFile(file, null);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException)
        {
            return null;
        }
    }

    /// <summary>IPv4/IPv6 addresses of the machine's up interfaces (what a player on the LAN would type).</summary>
    private static IEnumerable<IPAddress> LocalAddresses()
    {
        NetworkInterface[] interfaces;
        try
        {
            interfaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            yield break;
        }

        foreach (var nic in interfaces)
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            IPInterfaceProperties properties;
            try
            {
                properties = nic.GetIPProperties();
            }
            catch (NetworkInformationException)
            {
                continue;
            }

            foreach (var unicast in properties.UnicastAddresses)
            {
                if (unicast.Address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                {
                    yield return unicast.Address;
                }
            }
        }
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace X4MP.Launcher.Core;

/// <summary>Response of <c>GET /api/v1/join/nonce</c>.</summary>
public sealed record JoinNonceResponse(string Nonce, bool PasswordRequired, int ExpiresInSeconds);

/// <summary>The session's mod set, as the server publishes it (docs/mod-management.md 4.1).</summary>
public sealed record JoinManifest(
    string Session,
    long PolicyVersion,
    string GameBuild,
    string UnknownDefault,
    List<JoinMod> Mods);

/// <summary>One row of the manifest. Rule is the rule the server enforces; Version is empty when any version will do.</summary>
public sealed record JoinMod(
    string Id,
    string Name,
    string Rule,
    bool Enabled,
    string Class,
    string Version,
    long WorkshopId,
    string? NexusUrl,
    string Notes);

/// <summary>A failure the player can act on, with the server's problem code when it sent one.</summary>
public sealed class JoinClientException : Exception
{
    public JoinClientException(string code, string message) : base(message) => Code = code;

    public string Code { get; }
}

/// <summary>The proof the join endpoints expect: HMAC-SHA256(key = SHA-256(password), nonce || player key).</summary>
public static class JoinAuthCodes
{
    public static byte[] ComputeProof(string password, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> playerKey)
    {
        ArgumentNullException.ThrowIfNull(password);
        var key = SHA256.HashData(Encoding.UTF8.GetBytes(password));
        var message = new byte[nonce.Length + playerKey.Length];
        nonce.CopyTo(message);
        playerKey.CopyTo(message.AsSpan(nonce.Length));
        return HMACSHA256.HashData(key, message);
    }
}

/// <summary>
/// Fetches the mod manifest from a server. An open server answers without credentials; a protected one needs the
/// join password, proved against a single-use nonce that this class requests first (M6-X1 contract).
/// </summary>
public sealed class JoinManifestClient(HttpClient http)
{
    private const string NonceHeader = "X-X4MP-Nonce";
    private const string PlayerKeyHeader = "X-X4MP-Player-Key";
    private const string ProofHeader = "X-X4MP-Proof";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<JoinManifest> FetchAsync(string address, string? password, CancellationToken cancellationToken)
    {
        var baseUri = BaseUri(address);
        var nonce = await GetAsync<JoinNonceResponse>(new Uri(baseUri, "api/v1/join/nonce"), null, cancellationToken).ConfigureAwait(false);

        Dictionary<string, string>? headers = null;
        if (nonce.PasswordRequired)
        {
            if (string.IsNullOrEmpty(password))
            {
                throw new JoinClientException("JoinAuthRequired", "This server has a join password. Enter it and check again.");
            }

            if (!TryFromHex(nonce.Nonce, out var nonceBytes))
            {
                throw new JoinClientException("BadResponse", "The server sent a nonce the launcher cannot read.");
            }

            var playerKey = RandomNumberGenerator.GetBytes(32);
            var proof = JoinAuthCodes.ComputeProof(password, nonceBytes, playerKey);
            headers = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [NonceHeader] = Convert.ToHexStringLower(nonceBytes),
                [PlayerKeyHeader] = Convert.ToHexStringLower(playerKey),
                [ProofHeader] = Convert.ToHexStringLower(proof),
            };
        }

        return await GetAsync<JoinManifest>(new Uri(baseUri, "api/v1/join/mod-manifest"), headers, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> GetAsync<T>(Uri uri, IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (headers is not null)
        {
            foreach (var (name, value) in headers) request.Headers.TryAddWithoutValidation(name, value);
        }

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await FailureAsync(response, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var value = await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken).ConfigureAwait(false);
            return value ?? throw new JoinClientException("BadResponse", "The server sent an empty answer.");
        }
        catch (JsonException)
        {
            throw new JoinClientException("BadResponse", "The server answered with something the launcher cannot read.");
        }
    }

    private static async Task<JoinClientException> FailureAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string? code = null;
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var problem = JsonDocument.Parse(body);
            if (problem.RootElement.TryGetProperty("code", out var element) && element.ValueKind == JsonValueKind.String)
            {
                code = element.GetString();
            }
        }
        catch (JsonException)
        {
            // A non-problem body (a bare 404 from an old build, a proxy page) is fine: the status code still says enough.
        }

        int status = (int)response.StatusCode;
        string message = code switch
        {
            "JoinAuthRequired" or "Unauthorized" => "This server has a join password. Enter it and check again.",
            "JoinAuthFailed" => "The join password was not accepted.",
            "JoinNonceExpired" => "The server took too long to check the password. Check again.",
            "NetworkNotAllowed" => "The server does not accept connections from this network. Your host may need to allow it on the admin port.",
            "NotFound" => "This server runs an older build and has no launcher endpoints yet.",
            _ when status == (int)HttpStatusCode.Forbidden => "The server refused the request.",
            _ => $"The server answered {status} {response.ReasonPhrase}.",
        };
        return new JoinClientException(code ?? "Http" + status, message);
    }

    private static Uri BaseUri(string address)
    {
        try
        {
            return ServerAddress.Normalise(address);
        }
        catch (ArgumentException error)
        {
            // The same function builds the trust key, so a server that answers is always remembered under one name.
            var code = string.IsNullOrWhiteSpace(address) ? "NoAddress" : "BadAddress";
            throw new JoinClientException(code, error.Message);
        }
    }

    private static bool TryFromHex(string? hex, out byte[] bytes)
    {
        bytes = [];
        if (hex is null || hex.Length != 64) return false;
        try
        {
            bytes = Convert.FromHexString(hex);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

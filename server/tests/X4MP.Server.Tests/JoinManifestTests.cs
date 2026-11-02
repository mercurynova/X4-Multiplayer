using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using X4MP.Core.Mods;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.Server.Tests;

/// <summary>The launcher's two endpoints (M6-X1, mod-management 4.1): the joined session's mod manifest and the join-password nonce.</summary>
public class JoinManifestTests
{
    private const string NonceHeader = "X-X4MP-Nonce";
    private const string KeyHeader = "X-X4MP-Player-Key";
    private const string ProofHeader = "X-X4MP-Proof";
    private const string Password = "correct horse battery staple";

    private static async Task<JsonDocument> GetJson(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static Task<HttpResponseMessage> GetManifest(HttpClient client, string? nonce = null, string? key = null, string? proof = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/join/mod-manifest");
        if (nonce is not null)
        {
            request.Headers.Add(NonceHeader, nonce);
        }

        if (key is not null)
        {
            request.Headers.Add(KeyHeader, key);
        }

        if (proof is not null)
        {
            request.Headers.Add(ProofHeader, proof);
        }

        return client.SendAsync(request);
    }

    private static async Task<string?> ProblemCode(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("code").GetString();
    }

    [Fact]
    public async Task NonceIsFreshHexAndSaysWhetherAPasswordIsNeeded()
    {
        await using var factory = new AuthFactory();
        using var client = factory.NewClient();

        using var first = await GetJson(client, "/api/v1/join/nonce");
        using var second = await GetJson(client, "/api/v1/join/nonce");

        string a = first.RootElement.GetProperty("nonce").GetString()!;
        string b = second.RootElement.GetProperty("nonce").GetString()!;
        Assert.Equal(HandshakeAuth.NonceLength * 2, a.Length);
        Assert.True(a.All(char.IsAsciiHexDigitLower));
        Assert.NotEqual(a, b);
        Assert.False(first.RootElement.GetProperty("passwordRequired").GetBoolean());
        Assert.Equal(120, first.RootElement.GetProperty("expiresInSeconds").GetInt32());
    }

    [Fact]
    public async Task ManifestListsTheEffectiveRulesAndDerivedWorkshopIds()
    {
        await using var factory = new AuthFactory();
        using var client = factory.NewClient();
        var editor = factory.Services.GetRequiredService<IModPolicyEditor>();
        editor.Update("test", p =>
        {
            p.Entries ??= [];
            p.Entries.Clear();
            p.Entries.Add(new ModPolicyEntryT
            {
                Id = "ws_1234567890",
                Name = "Better Traders",
                Rule = ModRule.Required,
                Enabled = true,
                VersionRule = VersionRule.AtLeast,
                Version = "1.3",
            });
            p.Entries.Add(new ModPolicyEntryT { Id = "off_mod", Name = "Off", Rule = ModRule.Required, Enabled = false });
            p.Entries.Add(new ModPolicyEntryT
            {
                Id = "nexus_mod",
                Name = "Nexus Mod",
                Rule = ModRule.Allowed,
                Enabled = true,
                NexusUrl = "https://www.nexusmods.com/x4foundations/mods/42",
                Notes = "needs the 9.00 build",
            });
        });

        using var doc = await GetJson(client, "/api/v1/join/mod-manifest");
        var root = doc.RootElement;
        Assert.Equal("X4MP Server", root.GetProperty("session").GetString());
        Assert.Equal(editor.Current.Version, root.GetProperty("policyVersion").GetInt64());
        Assert.Equal("900-611726", root.GetProperty("gameBuild").GetString());
        Assert.Equal(editor.Current.UnknownDefault.ToString(), root.GetProperty("unknownDefault").GetString());

        var mods = root.GetProperty("mods").EnumerateArray().ToDictionary(m => m.GetProperty("id").GetString()!);
        Assert.Equal(3, mods.Count);

        var traders = mods["ws_1234567890"];
        Assert.Equal("Better Traders", traders.GetProperty("name").GetString());
        Assert.Equal("Required", traders.GetProperty("rule").GetString());
        Assert.True(traders.GetProperty("enabled").GetBoolean());
        Assert.Equal("Sim", traders.GetProperty("class").GetString());
        Assert.Equal("1.3", traders.GetProperty("version").GetString());
        Assert.Equal(1234567890, traders.GetProperty("workshopId").GetInt64());
        Assert.Equal(JsonValueKind.Null, traders.GetProperty("nexusUrl").ValueKind);

        // A Required entry the admin switched off is enforced, and therefore published, as Blocked.
        var off = mods["off_mod"];
        Assert.Equal("Blocked", off.GetProperty("rule").GetString());
        Assert.False(off.GetProperty("enabled").GetBoolean());

        // Links are re-validated on the way out: the stored URL comes back canonical, and the notes travel with it.
        var nexus = mods["nexus_mod"];
        Assert.Equal("https://www.nexusmods.com/x4foundations/mods/42", nexus.GetProperty("nexusUrl").GetString());
        Assert.Equal("needs the 9.00 build", nexus.GetProperty("notes").GetString());
        Assert.Equal(string.Empty, nexus.GetProperty("version").GetString());
    }

    [Fact]
    public async Task PasswordProtectedManifestNeedsAProofAndSpendsTheNonceOnce()
    {
        await using var factory = new AuthFactory(new Dictionary<string, string>
        {
            ["X4MP:Net:JoinPassword"] = Password,
            ["X4MP:Net:ServerName"] = "Friday Run",
        });
        using var client = factory.NewClient();

        using var missing = await GetManifest(client);
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Equal("application/problem+json", missing.Content.Headers.ContentType?.MediaType);
        Assert.Equal("JoinAuthRequired", await ProblemCode(missing));

        using var nonceDoc = await GetJson(client, "/api/v1/join/nonce");
        Assert.True(nonceDoc.RootElement.GetProperty("passwordRequired").GetBoolean());
        byte[] nonce = Convert.FromHexString(nonceDoc.RootElement.GetProperty("nonce").GetString()!);
        byte[] key = RandomNumberGenerator.GetBytes(HandshakeAuth.PlayerKeyLength);

        using var wrong = await GetManifest(
            client, Convert.ToHexStringLower(nonce), Convert.ToHexStringLower(key), Convert.ToHexStringLower(new byte[HandshakeAuth.ProofLength]));
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal("JoinAuthFailed", await ProblemCode(wrong));

        // The failed attempt spent the nonce, so the same one cannot be replayed with the right proof.
        string proof = Convert.ToHexStringLower(HandshakeAuth.ComputeProof(Password, nonce, key));
        using var replay = await GetManifest(client, Convert.ToHexStringLower(nonce), Convert.ToHexStringLower(key), proof);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Equal("JoinNonceExpired", await ProblemCode(replay));

        byte[] unknownNonce = RandomNumberGenerator.GetBytes(HandshakeAuth.NonceLength);
        using var unknown = await GetManifest(
            client, Convert.ToHexStringLower(unknownNonce), Convert.ToHexStringLower(key), Convert.ToHexStringLower(new byte[HandshakeAuth.ProofLength]));
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal("JoinNonceExpired", await ProblemCode(unknown));

        using var fresh = await GetJson(client, "/api/v1/join/nonce");
        byte[] good = Convert.FromHexString(fresh.RootElement.GetProperty("nonce").GetString()!);
        using var accepted = await GetManifest(
            client, Convert.ToHexStringLower(good), Convert.ToHexStringLower(key), Convert.ToHexStringLower(HandshakeAuth.ComputeProof(Password, good, key)));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        string body = await accepted.Content.ReadAsStringAsync();
        using var manifest = JsonDocument.Parse(body);
        Assert.Equal("Friday Run", manifest.RootElement.GetProperty("session").GetString());
        Assert.DoesNotContain(Password, body, StringComparison.Ordinal);
        Assert.DoesNotContain("joinPassword", body, StringComparison.OrdinalIgnoreCase);
    }
}

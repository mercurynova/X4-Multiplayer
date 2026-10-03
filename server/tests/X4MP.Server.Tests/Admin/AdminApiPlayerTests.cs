using System.Diagnostics;
using System.Net;
using X4MP.Core.Session;
using X4MP.Persistence;
using X4MP.Proto;
using X4MP.Protocol.Client;

namespace X4MP.Server.Tests.Admin;

/// <summary>Players, kick, mute, notes, name release and bans over the real server with fake nodes (acceptance 2, 3).</summary>
public sealed class AdminApiPlayerTests(AdminServerFixture f) : IClassFixture<AdminServerFixture>
{
    private HttpClient Admin => f.Admin;

    // ------------------------------------------------------------------ list and detail

    [Fact]
    public async Task ListAndDetailShowAnOnlinePlayerWithItsKeyHash()
    {
        string name = f.NextName("Lister");
        var (node, id) = await f.JoinAsync(name);
        await using var joined = node;

        using var list = await f.Viewer.GetAsync($"/api/v1/players?online=true&q={name}");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var row = (await list.JsonAsync()).EnumerateArray().Single();
        Assert.Equal(id, row.GetProperty("id").GetInt64());
        Assert.True(row.GetProperty("online").GetBoolean());
        Assert.False(row.GetProperty("muted").GetBoolean());
        Assert.Equal(JsonValueKindNull, row.GetProperty("activeBan").ValueKind);

        using var offline = await f.Viewer.GetAsync($"/api/v1/players?online=false&q={name}");
        Assert.Empty((await offline.JsonAsync()).EnumerateArray());

        using var detail = await f.Viewer.GetAsync($"/api/v1/players/{id}");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        var body = await detail.JsonAsync();
        Assert.Equal(name, body.GetProperty("player").GetProperty("name").GetString());
        Assert.Equal(64, body.GetProperty("keyHash").GetString()!.Length);
        Assert.Equal(name, body.GetProperty("live").GetProperty("name").GetString());
    }

    private const System.Text.Json.JsonValueKind JsonValueKindNull = System.Text.Json.JsonValueKind.Null;

    [Fact]
    public async Task PlayerReadsRejectBadInputWithProblems()
    {
        using (var response = await f.Viewer.GetAsync("/api/v1/players?online=maybe"))
        {
            await response.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "online");
        }

        using (var response = await f.Viewer.GetAsync("/api/v1/players?limit=0"))
        {
            await response.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "limit");
        }

        using (var response = await f.Viewer.GetAsync("/api/v1/players/987654"))
        {
            await response.AssertProblemAsync(HttpStatusCode.NotFound, "NotFound");
        }
    }

    // ------------------------------------------------------------------ kick

    [Fact]
    public async Task KickDisconnectsTheNodeWithinOneSecondAndItMayComeBack()
    {
        string name = f.NextName("Kicked");
        var (node, id) = await f.JoinAsync(name);
        await using var joined = node;

        var clock = Stopwatch.StartNew();
        using var response = await Admin.CallAsync(HttpMethod.Post, $"/api/v1/players/{id}/kick", new { reason = "afk griefing" });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await Api.AssertDisconnectedAsync(node, DisconnectCode.Kicked, TimeSpan.FromSeconds(5));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"kick took {clock.Elapsed}");

        var row = await Api.WaitForAuditAsync(f.Server, "player.kick", id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Contains("afk griefing", row.DataJson, StringComparison.Ordinal);
        Assert.StartsWith("test-", row.Actor, StringComparison.Ordinal);

        // a kick is not a ban
        await using var again = await f.Server.ConnectAsync(name, Role.Client);
        Assert.NotNull(again.Welcome);
    }

    [Fact]
    public async Task KickErrorsAreProblems()
    {
        long offline = await f.CreateOfflinePlayerAsync(f.NextName("Offline"));

        using (var response = await Admin.CallAsync(HttpMethod.Post, $"/api/v1/players/{offline}/kick", new { reason = " " }))
        {
            await response.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "reason");
        }

        using (var response = await Admin.CallAsync(HttpMethod.Post, "/api/v1/players/987654/kick", new { reason = "x" }))
        {
            await response.AssertProblemAsync(HttpStatusCode.NotFound, "NotFound");
        }

        using (var response = await Admin.CallAsync(HttpMethod.Post, $"/api/v1/players/{offline}/kick", new { reason = "x" }))
        {
            await response.AssertProblemAsync(HttpStatusCode.Conflict, "PlayerNotOnline");
        }

        using (var response = await Admin.CallAsync(HttpMethod.Post, $"/api/v1/players/{offline}/kick", "{ not json"))
        {
            await response.AssertProblemAsync(HttpStatusCode.BadRequest, "InvalidRequest");
        }
    }

    // ------------------------------------------------------------------ avatars (M3-01, plan Q6)

    private static uint _avatarIds = 7000;

    /// <summary>Puts a player avatar into the server's mirror, as the authority's <c>EntitySpawn</c> would.</summary>
    private async Task<uint> SpawnAvatarAsync(long playerId, ushort controller)
    {
        uint netId = Interlocked.Increment(ref _avatarIds);
        var payload = X4MP.Protocol.MessageEncoder.EncodePayload(
            b => EntitySpawn.Pack(b, new EntitySpawnT
            {
                Entities =
                [
                    new EntityRecordT
                    {
                        NetId = netId, Kind = EntityKind.ShipS, Origin = EntityOrigin.PlayerShip, OwnerTeam = 1, OwnerPlayer = (ushort)playerId,
                        ControllerPlayer = controller, Name = "Pilot", Idcode = "AVA-" + netId, Hull = 255, Shield = 255,
                        State = new EntityStateT { NetId = netId, Sector = 1, Px = 640 },
                    },
                ],
            }),
            512);
        var spawn = X4MP.Protocol.MessageRegistry.Default.Decode<EntitySpawn>(
            new X4MP.Protocol.Frame(MsgType.EntitySpawn, X4MP.Protocol.FrameOptions.None, X4MP.Protocol.Lane.Control, payload));
        await f.Server.Actor.CallAsync(() =>
        {
            f.Server.World.ApplySpawn(spawn);
            return true;
        });
        return netId;
    }

    private Task<bool> InMirrorAsync(uint netId) => f.Server.Actor.CallAsync(() => f.Server.World.TryGet(netId, out _));

    [Fact]
    public async Task KickLeavesTheAvatarParkedUnlessTheAdminAsksToRemoveIt()
    {
        var (keep, keepId) = await f.JoinAsync(f.NextName("Parked"));
        var (drop, dropId) = await f.JoinAsync(f.NextName("Removed"));
        await using var k = keep;
        await using var d = drop;
        uint keepAvatar = await SpawnAvatarAsync(keepId, (ushort)keepId);
        uint dropAvatar = await SpawnAvatarAsync(dropId, (ushort)dropId);

        using (var response = await Admin.CallAsync(HttpMethod.Post, $"/api/v1/players/{keepId}/kick", new { reason = "plain kick" }))
        {
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }

        await Api.AssertDisconnectedAsync(keep, DisconnectCode.Kicked, TimeSpan.FromSeconds(5));
        Assert.True(await InMirrorAsync(keepAvatar), "a plain kick leaves the avatar in the universe (parked)");

        using (var response = await Admin.CallAsync(HttpMethod.Post, $"/api/v1/players/{dropId}/kick", new { reason = "grief", removeAvatar = true }))
        {
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }

        await Api.AssertDisconnectedAsync(drop, DisconnectCode.Kicked, TimeSpan.FromSeconds(5));
        Assert.False(await InMirrorAsync(dropAvatar), "kick with removeAvatar removes the ship");
        Assert.True(await InMirrorAsync(keepAvatar));
        var row = await Api.WaitForAuditAsync(f.Server, "player.kick", dropId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Contains("\"avatarsRemoved\":\"1\"", row.DataJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemovingTheAvatarOfAnOfflinePlayerWorksThroughKickWithTheOptionAndPlainKickStillConflicts()
    {
        long offline = await f.CreateOfflinePlayerAsync(f.NextName("Gone"));
        uint avatar = await SpawnAvatarAsync(offline, 0); // parked: its player left

        using (var response = await Admin.CallAsync(HttpMethod.Post, $"/api/v1/players/{offline}/kick", new { reason = "x" }))
        {
            await response.AssertProblemAsync(HttpStatusCode.Conflict, "PlayerNotOnline");
        }

        Assert.True(await InMirrorAsync(avatar));

        using (var response = await Admin.CallAsync(HttpMethod.Post, $"/api/v1/players/{offline}/kick", new { reason = "clean up", removeAvatar = true }))
        {
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }

        Assert.False(await InMirrorAsync(avatar));
    }

    [Fact]
    public async Task BanWithRemoveAvatarAlsoRemovesTheShipsOfTheBannedPlayer()
    {
        var (node, id) = await f.JoinAsync(f.NextName("BanShip"));
        await using var joined = node;
        uint avatar = await SpawnAvatarAsync(id, (ushort)id);

        using var create = await Admin.CallAsync(HttpMethod.Post, "/api/v1/bans", new { playerId = id, reason = "cheating", durationMinutes = 5, removeAvatar = true });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        await Api.AssertDisconnectedAsync(node, DisconnectCode.Banned, TimeSpan.FromSeconds(5));
        Assert.False(await InMirrorAsync(avatar));
        long banId = (await create.JsonAsync()).GetProperty("id").GetInt64();
        var row = await Api.WaitForAuditAsync(f.Server, "ban.create", banId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Contains("\"avatarsRemoved\":\"1\"", row.DataJson, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ mute

    [Fact]
    public async Task MuteAndUnmuteArePersistedAndAudited()
    {
        string name = f.NextName("Muted");
        var (node, id) = await f.JoinAsync(name);
        await using var joined = node;

        using (var response = await Admin.CallAsync(HttpMethod.Post, $"/api/v1/players/{id}/mute", new { minutes = 30, reason = "spam" }))
        {
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        using (var detail = await Admin.GetAsync($"/api/v1/players/{id}"))
        {
            var player = (await detail.JsonAsync()).GetProperty("player");
            Assert.True(player.GetProperty("muted").GetBoolean());
            Assert.NotEqual(JsonValueKindNull, player.GetProperty("mutedUntil").ValueKind);
        }

        var muteRow = await Api.WaitForAuditAsync(f.Server, "player.mute", id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Contains("spam", muteRow.DataJson, StringComparison.Ordinal);

        using (var response = await Admin.CallAsync(HttpMethod.Delete, $"/api/v1/players/{id}/mute"))
        {
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        using (var response = await Admin.CallAsync(HttpMethod.Delete, $"/api/v1/players/{id}/mute"))
        {
            await response.AssertProblemAsync(HttpStatusCode.Conflict, "NotMuted");
        }

        await Api.WaitForAuditAsync(f.Server, "player.unmute", id.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task MuteErrorsAreProblems()
    {
        long offline = await f.CreateOfflinePlayerAsync(f.NextName("Offline"));
        using (var response = await Admin.CallAsync(HttpMethod.Post, $"/api/v1/players/{offline}/mute", new { minutes = 0 }))
        {
            var problem = await response.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "minutes");
            Assert.True(problem.GetProperty("errors").TryGetProperty("reason", out _)); // every invalid field is listed
        }

        using (var response = await Admin.CallAsync(HttpMethod.Post, "/api/v1/players/987654/mute", new { reason = "x" }))
        {
            await response.AssertProblemAsync(HttpStatusCode.NotFound, "NotFound");
        }

        using (var response = await Admin.CallAsync(HttpMethod.Delete, "/api/v1/players/987654/mute"))
        {
            await response.AssertProblemAsync(HttpStatusCode.NotFound, "NotFound");
        }
    }

    // ------------------------------------------------------------------ notes and name release

    [Fact]
    public async Task NotesAndNameReleaseWork()
    {
        string name = f.NextName("Noted");
        long offline = await f.CreateOfflinePlayerAsync(name);

        using (var response = await Admin.CallAsync(HttpMethod.Patch, $"/api/v1/players/{offline}", new { notes = "trusted, owes a favour" }))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("trusted, owes a favour", (await response.JsonAsync()).GetProperty("notes").GetString());
        }

        using (var response = await Admin.CallAsync(HttpMethod.Patch, $"/api/v1/players/{offline}", new { releaseName = true }))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.StartsWith("released-", (await response.JsonAsync()).GetProperty("name").GetString(), StringComparison.Ordinal);
        }

        // the name is free for another key now
        var store = f.Server.Service<IPlayerStore>();
        var other = await store.BindAsync(name, System.Security.Cryptography.SHA256.HashData([1, 2, 3]), IPAddress.Loopback, DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Equal(PlayerBindStatus.Ok, other.Status);

        await Api.WaitForAuditAsync(f.Server, "player.notes", offline.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await Api.WaitForAuditAsync(f.Server, "player.release-name", offline.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task PatchPlayerErrorsAreProblems()
    {
        long offline = await f.CreateOfflinePlayerAsync(f.NextName("Offline"));
        using (var response = await Admin.CallAsync(HttpMethod.Patch, $"/api/v1/players/{offline}", new { }))
        {
            await response.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "body");
        }

        using (var response = await Admin.CallAsync(HttpMethod.Patch, $"/api/v1/players/{offline}", new { notes = new string('x', 1001) }))
        {
            await response.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "notes");
        }

        using (var response = await Admin.CallAsync(HttpMethod.Patch, "/api/v1/players/987654", new { notes = "x" }))
        {
            await response.AssertProblemAsync(HttpStatusCode.NotFound, "NotFound");
        }

        var (node, id) = await f.JoinAsync(f.NextName("Present"));
        await using var joined = node;
        using (var response = await Admin.CallAsync(HttpMethod.Patch, $"/api/v1/players/{id}", new { releaseName = true }))
        {
            await response.AssertProblemAsync(HttpStatusCode.Conflict, "PlayerOnline");
        }
    }

    [Fact]
    public async Task TeleportViewIsNotImplemented()
    {
        using var response = await Admin.CallAsync(HttpMethod.Post, "/api/v1/players/1/teleport-view", new { sectorId = 1 });
        await response.AssertProblemAsync(HttpStatusCode.NotImplemented, "NotImplemented");
    }

    // ------------------------------------------------------------------ bans: key

    [Fact]
    public async Task BanByPlayerKicksTheNodeBlocksItsReconnectAndUnbanAllowsIt()
    {
        string name = f.NextName("Banned");
        var (node, id) = await f.JoinAsync(name);
        await using var joined = node;

        var clock = Stopwatch.StartNew();
        using var create = await Admin.CallAsync(HttpMethod.Post, "/api/v1/bans", new { playerId = id, reason = "cheating", durationMinutes = 60 });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var ban = await create.JsonAsync();
        long banId = ban.GetProperty("id").GetInt64();
        Assert.Equal(id, ban.GetProperty("playerId").GetInt64());
        Assert.Equal(name, ban.GetProperty("playerName").GetString());
        Assert.True(ban.GetProperty("active").GetBoolean());
        Assert.NotEqual(JsonValueKindNull, ban.GetProperty("expiresAt").ValueKind);

        await Api.AssertDisconnectedAsync(node, DisconnectCode.Banned, TimeSpan.FromSeconds(5));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"ban took {clock.Elapsed}");

        var rejected = await Assert.ThrowsAsync<HandshakeRejectedException>(() => f.Server.ConnectAsync(name, Role.Client));
        Assert.Equal(DisconnectCode.Banned, rejected.Code);
        Assert.Equal("cheating", rejected.ServerMessage);

        using (var list = await f.Viewer.GetAsync("/api/v1/bans?active=true"))
        {
            Assert.Contains((await list.JsonAsync()).EnumerateArray(), b => b.GetProperty("id").GetInt64() == banId);
        }

        using (var player = await f.Viewer.GetAsync($"/api/v1/players/{id}"))
        {
            var body = await player.JsonAsync();
            Assert.Equal(banId, body.GetProperty("player").GetProperty("activeBan").GetProperty("id").GetInt64());
            Assert.Single(body.GetProperty("bans").EnumerateArray());
        }

        using (var duplicate = await Admin.CallAsync(HttpMethod.Post, "/api/v1/bans", new { playerId = id, reason = "again" }))
        {
            await duplicate.AssertProblemAsync(HttpStatusCode.Conflict, "AlreadyBanned");
        }

        using (var revoke = await Admin.CallAsync(HttpMethod.Delete, $"/api/v1/bans/{banId}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        }

        using (var again = await Admin.CallAsync(HttpMethod.Delete, $"/api/v1/bans/{banId}"))
        {
            await again.AssertProblemAsync(HttpStatusCode.Conflict, "AlreadyRevoked");
        }

        await using var back = await f.Server.ConnectAsync(name, Role.Client);
        Assert.Equal(id, back.Welcome.PlayerId);

        using (var inactive = await f.Viewer.GetAsync("/api/v1/bans?active=false"))
        {
            Assert.Contains((await inactive.JsonAsync()).EnumerateArray(), b => b.GetProperty("id").GetInt64() == banId);
        }

        var created = await Api.WaitForAuditAsync(f.Server, "ban.create", banId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Contains("cheating", created.DataJson, StringComparison.Ordinal);
        await Api.WaitForAuditAsync(f.Server, "ban.revoke", banId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task BanByKeyHashResolvesTheKnownPlayer()
    {
        string name = f.NextName("KeyBanned");
        var (node, id) = await f.JoinAsync(name);
        await using var joined = node;
        string keyHash;
        using (var detail = await Admin.GetAsync($"/api/v1/players/{id}"))
        {
            keyHash = (await detail.JsonAsync()).GetProperty("keyHash").GetString()!;
        }

        using var create = await Admin.CallAsync(HttpMethod.Post, "/api/v1/bans", new { keyHash, reason = "key ban" });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        long banId = (await create.JsonAsync()).GetProperty("id").GetInt64();
        await Api.AssertDisconnectedAsync(node, DisconnectCode.Banned, TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<HandshakeRejectedException>(() => f.Server.ConnectAsync(name, Role.Client));

        using var revoke = await Admin.CallAsync(HttpMethod.Delete, $"/api/v1/bans/{banId}");
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
    }

    // ------------------------------------------------------------------ bans: network

    [Fact]
    public async Task BanByCidrKicksNodesInsideItBlocksReconnectsAndUnbanAllowsThem()
    {
        string name = f.NextName("NetBanned");
        var (node, _) = await f.JoinAsync(name);
        await using var __ = node;

        // host bits are masked: 127.7.7.7/8 is the network 127.0.0.0/8, which holds the loopback address the node connects from
        using var create = await Admin.CallAsync(HttpMethod.Post, "/api/v1/bans", new { ipCidr = "127.7.7.7/8", reason = "bad network" });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var ban = await create.JsonAsync();
        long banId = ban.GetProperty("id").GetInt64();
        try
        {
            Assert.Equal("127.0.0.0/8", ban.GetProperty("ipCidr").GetString());
            await Api.AssertDisconnectedAsync(node, DisconnectCode.Banned, TimeSpan.FromSeconds(5));

            // refused before the handshake: the client sees the Disconnect (or the closed socket)
            var failure = await Assert.ThrowsAnyAsync<Exception>(() => f.Server.ConnectAsync(f.NextName("Other"), Role.Client));
            if (failure is HandshakeRejectedException rejected)
            {
                Assert.Equal(DisconnectCode.Banned, rejected.Code);
            }
        }
        finally
        {
            using var revoke = await Admin.CallAsync(HttpMethod.Delete, $"/api/v1/bans/{banId}");
            Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        }

        await using var back = await f.Server.ConnectAsync(name, Role.Client);
        Assert.NotNull(back.Welcome);
    }

    [Fact]
    public async Task BanErrorsAreProblems()
    {
        using (var response = await Admin.CallAsync(HttpMethod.Post, "/api/v1/bans", new { reason = "x" }))
        {
            await response.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "target");
        }

        using (var response = await Admin.CallAsync(HttpMethod.Post, "/api/v1/bans", new { ipCidr = "not-an-ip", reason = "x" }))
        {
            await response.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "ipCidr");
        }

        using (var response = await Admin.CallAsync(HttpMethod.Post, "/api/v1/bans", new { ipCidr = "10.0.0.0/33", reason = "x" }))
        {
            await response.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "ipCidr");
        }

        using (var response = await Admin.CallAsync(HttpMethod.Post, "/api/v1/bans", new { ipCidr = "10.0.0.0/8" }))
        {
            await response.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "reason");
        }

        using (var response = await Admin.CallAsync(HttpMethod.Post, "/api/v1/bans", new { keyHash = "zz", reason = "x" }))
        {
            await response.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "keyHash");
        }

        using (var response = await Admin.CallAsync(HttpMethod.Post, "/api/v1/bans", new { ipCidr = "10.0.0.0/8", reason = "x", durationMinutes = 0 }))
        {
            await response.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "durationMinutes");
        }

        using (var response = await Admin.CallAsync(HttpMethod.Post, "/api/v1/bans", new { playerId = 987654, reason = "x" }))
        {
            await response.AssertProblemAsync(HttpStatusCode.NotFound, "NotFound");
        }

        using (var response = await Admin.CallAsync(HttpMethod.Post, "/api/v1/bans", new { keyHash = new string('a', 64), reason = "x" }))
        {
            await response.AssertProblemAsync(HttpStatusCode.NotFound, "NotFound");
        }

        using (var response = await Admin.CallAsync(HttpMethod.Delete, "/api/v1/bans/987654"))
        {
            await response.AssertProblemAsync(HttpStatusCode.NotFound, "NotFound");
        }

        using (var response = await f.Viewer.GetAsync("/api/v1/bans?active=perhaps"))
        {
            await response.AssertProblemAsync(HttpStatusCode.BadRequest, "ValidationFailed", "active");
        }
    }

    [Fact]
    public async Task AnExpiredBanNoLongerBlocksAndIsNotActive()
    {
        string name = f.NextName("Expiring");
        long id = await f.CreateOfflinePlayerAsync(name);
        var queries = f.Server.Service<SqliteAdminQueries>();
        long banId = queries.InsertBan(id, null, "short", "test", DateTimeOffset.UtcNow.AddMinutes(-10), DateTimeOffset.UtcNow.AddMinutes(-1));

        using var list = await f.Viewer.GetAsync("/api/v1/bans?active=true");
        Assert.DoesNotContain((await list.JsonAsync()).EnumerateArray(), b => b.GetProperty("id").GetInt64() == banId);
        using var all = await f.Viewer.GetAsync("/api/v1/bans");
        var row = (await all.JsonAsync()).EnumerateArray().Single(b => b.GetProperty("id").GetInt64() == banId);
        Assert.False(row.GetProperty("active").GetBoolean());
    }
}

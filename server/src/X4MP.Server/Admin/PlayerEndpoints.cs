using System.Globalization;
using System.Net;
using X4MP.Core.Relay;
using X4MP.Persistence;
using X4MP.Proto;
using X4MP.Server.Api;
using X4MP.Server.Auth;

namespace X4MP.Server.Admin;

/// <summary><c>/api/v1/players</c> and <c>/api/v1/bans</c>: roster, kick, mute, notes, name release, key and network bans.</summary>
internal static class PlayerEndpoints
{
    private const int MaxNotesLength = 1000;
    private const int MaxBanMinutes = 5_256_000; // 10 years
    private const int MaxMuteMinutes = 525_600;  // 1 year

    public static void Map(IEndpointRouteBuilder routes)
    {
        var players = routes.MapGroup("/api/v1/players");
        players.MapGet("", ListPlayersAsync).RequireAuthorization(AdminPolicies.Viewer);
        players.MapGet("/{id:long}", GetPlayerAsync).RequireAuthorization(AdminPolicies.Viewer);
        players.MapPost("/{id:long}/kick", KickAsync).RequireAuthorization(AdminPolicies.Admin);
        players.MapPost("/{id:long}/mute", MuteAsync).RequireAuthorization(AdminPolicies.Admin);
        players.MapDelete("/{id:long}/mute", UnmuteAsync).RequireAuthorization(AdminPolicies.Admin);
        players.MapPost("/{id:long}/teleport-view", (long id) =>
            Problems.NotImplemented("Snapping a player's in-game camera is not available in this build."))
            .RequireAuthorization(AdminPolicies.Admin);
        players.MapPatch("/{id:long}", PatchPlayerAsync).RequireAuthorization(AdminPolicies.Admin);

        var bans = routes.MapGroup("/api/v1/bans");
        bans.MapGet("", ListBans).RequireAuthorization(AdminPolicies.Viewer);
        bans.MapPost("", CreateBanAsync).RequireAuthorization(AdminPolicies.Admin);
        bans.MapDelete("/{id:long}", RevokeBan).RequireAuthorization(AdminPolicies.Admin);
    }

    private static PlayerDto Dto(PlayerRecord player, LiveSession live, IReadOnlyList<BanRecord> activeBans, DateTimeOffset now)
    {
        live.Mutes.TryGetValue((int)player.Id, out var mute);
        var ban = activeBans.FirstOrDefault(b => b.PlayerId == player.Id);
        return AdminMapping.ToDto(player, live.IsConnected(player.Id), mute, ban, now);
    }

    // ------------------------------------------------------------------ players

    private static async Task<IResult> ListPlayersAsync(string? online, string? q, int? limit, AdminSessions sessions, SqliteAdminQueries queries)
    {
        var errors = new Dictionary<string, string[]>();
        bool? onlineOnly = null;
        if (!string.IsNullOrWhiteSpace(online))
        {
            if (bool.TryParse(online, out var flag))
            {
                onlineOnly = flag;
            }
            else
            {
                errors["online"] = ["Use true or false."];
            }
        }

        int count = AdminApi.Limit(limit, 200, 1000, errors);
        if (errors.Count > 0)
        {
            return Problems.Validation(errors);
        }

        var live = await sessions.GetLiveAsync();
        var now = sessions.Time.GetUtcNow();
        var onlineIds = live.Snapshot.Nodes.Where(n => n.Connected).Select(n => (long)n.PlayerId).ToHashSet();
        var rows = queries.ListPlayers(q, onlineOnly == true ? onlineIds : null, 1000, now)
            .Where(p => onlineOnly != false || !onlineIds.Contains(p.Id))
            .Take(count)
            .ToList();
        var bans = queries.ListBans(true, now);
        return Results.Json([.. rows.Select(p => Dto(p, live, bans, now))], ApiJsonContext.Default.ListPlayerDto);
    }

    private static async Task<IResult> GetPlayerAsync(long id, AdminSessions sessions, SqliteAdminQueries queries)
    {
        var now = sessions.Time.GetUtcNow();
        if (queries.FindPlayer(id, now) is not { } player)
        {
            return Problems.NotFound("The player");
        }

        var live = await sessions.GetLiveAsync();
        var bans = queries.ListBans(null, now).Where(b => b.PlayerId == id).ToList();
        var node = live.Snapshot.Nodes.FirstOrDefault(n => n.PlayerId == id);
        var detail = new PlayerDetailDto(
            Dto(player, live, bans.Where(b => b.IsActive(now)).ToList(), now),
            player.KeyHashHex,
            node is null ? null : AdminMapping.ToLive(node, now, live.Muted),
            [.. queries.PlayerHistory(id, 50).Select(h => new PlayerSessionDto(h.SessionId, h.SessionName, h.JoinedAt, h.LeftAt, h.LeaveReason, h.Role))],
            [.. bans.Select(b => AdminMapping.ToDto(b, now))]);
        return Results.Json(detail, ApiJsonContext.Default.PlayerDetailDto);
    }

    private static async Task<IResult> KickAsync(
        long id, KickRequest? body, HttpContext context, AdminSessions sessions, SqliteAdminQueries queries, AdminStore audit)
    {
        var errors = new Dictionary<string, string[]>();
        string? reason = AdminApi.CheckReason(body?.Reason, errors);
        if (errors.Count > 0)
        {
            return Problems.Validation(errors);
        }

        if (id > int.MaxValue || queries.FindPlayer(id, sessions.Time.GetUtcNow()) is null)
        {
            return Problems.NotFound("The player");
        }

        if (!await sessions.Actor.RemoveNodeAsync((int)id, DisconnectCode.Kicked, reason!))
        {
            return Problems.Conflict("PlayerNotOnline", "The player is not in the session.");
        }

        AdminApi.Audit(context, audit, "player.kick", id.ToString(CultureInfo.InvariantCulture), reason);
        return Results.Accepted();
    }

    private static async Task<IResult> MuteAsync(
        long id, MuteRequest? body, HttpContext context, AdminSessions sessions, SqliteAdminQueries queries, IChatControl chat)
    {
        var errors = new Dictionary<string, string[]>();
        string? reason = AdminApi.CheckReason(body?.Reason, errors);
        if (body?.Minutes is { } minutes && (minutes < 1 || minutes > MaxMuteMinutes))
        {
            errors["minutes"] = [$"Use a value from 1 to {MaxMuteMinutes}, or leave it out to mute until lifted."];
        }

        if (errors.Count > 0)
        {
            return Problems.Validation(errors);
        }

        if (id > int.MaxValue || queries.FindPlayer(id, sessions.Time.GetUtcNow()) is null)
        {
            return Problems.NotFound("The player");
        }

        // The relay audits the mute itself (action player.mute, with the reason and the end time) when it applies it.
        await chat.MuteAsync((int)id, body?.Minutes is { } m ? TimeSpan.FromMinutes(m) : null, AdminApi.Actor(context), reason);
        return Results.NoContent();
    }

    private static async Task<IResult> UnmuteAsync(long id, HttpContext context, AdminSessions sessions, SqliteAdminQueries queries, IChatControl chat)
    {
        if (id > int.MaxValue || queries.FindPlayer(id, sessions.Time.GetUtcNow()) is null)
        {
            return Problems.NotFound("The player");
        }

        return await chat.UnmuteAsync((int)id, AdminApi.Actor(context))
            ? Results.NoContent()
            : Problems.Conflict("NotMuted", "The player is not muted.");
    }

    private static async Task<IResult> PatchPlayerAsync(
        long id, PatchPlayerRequest? body, HttpContext context, AdminSessions sessions, SqliteAdminQueries queries, AdminStore audit)
    {
        var errors = new Dictionary<string, string[]>();
        if (body is null || (body.Notes is null && body.ReleaseName is null))
        {
            errors["body"] = ["Send notes and/or releaseName."];
        }
        else if (body.Notes is { Length: > MaxNotesLength })
        {
            errors["notes"] = [$"The notes can be at most {MaxNotesLength} characters."];
        }

        if (errors.Count > 0)
        {
            return Problems.Validation(errors);
        }

        var now = sessions.Time.GetUtcNow();
        if (queries.FindPlayer(id, now) is not { } player)
        {
            return Problems.NotFound("The player");
        }

        var live = await sessions.GetLiveAsync();
        if (body!.ReleaseName == true && live.IsInSession(id))
        {
            return Problems.Conflict("PlayerOnline", "Kick the player before releasing its name.");
        }

        string target = id.ToString(CultureInfo.InvariantCulture);
        if (body.Notes is not null)
        {
            queries.SetNotes(id, body.Notes.Length == 0 ? null : body.Notes);
            AdminApi.Audit(context, audit, "player.notes", target, null, new() { ["length"] = body.Notes.Length.ToString(CultureInfo.InvariantCulture) });
        }

        if (body.ReleaseName == true)
        {
            queries.ReleaseName(id);
            AdminApi.Audit(context, audit, "player.release-name", target, null, new() { ["name"] = player.Name });
        }

        var updated = queries.FindPlayer(id, now)!;
        return Results.Json(Dto(updated, live, queries.ListBans(true, now), now), ApiJsonContext.Default.PlayerDto);
    }

    // ------------------------------------------------------------------ bans

    private static IResult ListBans(string? active, SqliteAdminQueries queries, TimeProvider time)
    {
        bool? only = null;
        if (!string.IsNullOrWhiteSpace(active))
        {
            if (!bool.TryParse(active, out var flag))
            {
                return Problems.Validation("active", "Use true or false.");
            }

            only = flag;
        }

        var now = time.GetUtcNow();
        return Results.Json([.. queries.ListBans(only, now).Select(b => AdminMapping.ToDto(b, now))], ApiJsonContext.Default.ListBanDto);
    }

    private static async Task<IResult> CreateBanAsync(
        CreateBanRequest? body, HttpContext context, AdminSessions sessions, SqliteAdminQueries queries, AdminStore audit)
    {
        var errors = new Dictionary<string, string[]>();
        string? reason = AdminApi.CheckReason(body?.Reason, errors);
        if (body is null || (body.PlayerId is null && string.IsNullOrWhiteSpace(body.KeyHash) && string.IsNullOrWhiteSpace(body.IpCidr)))
        {
            errors["target"] = ["Give playerId, keyHash and/or ipCidr."];
        }

        string? cidr = null;
        IPNetwork network = default;
        if (!string.IsNullOrWhiteSpace(body?.IpCidr) && !AdminApi.TryNormalizeCidr(body.IpCidr, out cidr, out network))
        {
            errors["ipCidr"] = ["Use an address or a CIDR block such as 10.1.0.0/16."];
        }

        byte[]? keyHash = null;
        if (!string.IsNullOrWhiteSpace(body?.KeyHash))
        {
            try
            {
                keyHash = Convert.FromHexString(body.KeyHash.Trim());
            }
            catch (FormatException)
            {
                keyHash = null;
            }

            if (keyHash is not { Length: 32 })
            {
                errors["keyHash"] = ["keyHash is the 64-digit hex SHA-256 of the player key."];
            }
        }

        if (body?.DurationMinutes is { } minutes && (minutes < 1 || minutes > MaxBanMinutes))
        {
            errors["durationMinutes"] = [$"Use a value from 1 to {MaxBanMinutes}, or leave it out for a permanent ban."];
        }

        if (errors.Count > 0)
        {
            return Problems.Validation(errors);
        }

        var now = sessions.Time.GetUtcNow();
        PlayerRecord? player = null;
        if (body!.PlayerId is { } playerId)
        {
            player = queries.FindPlayer(playerId, now);
            if (player is null)
            {
                return Problems.NotFound("The player");
            }
        }

        if (keyHash is { Length: 32 })
        {
            var byKey = queries.FindPlayerByKeyHash(keyHash, now);
            if (byKey is null)
            {
                return Problems.NotFound("A player with that key hash");
            }

            if (player is not null && player.Id != byKey.Id)
            {
                return Problems.Validation("keyHash", "keyHash belongs to a different player than playerId.");
            }

            player = byKey;
        }

        var active = queries.ListBans(true, now);
        if ((player is not null && active.Any(b => b.PlayerId == player.Id)) || (cidr is not null && active.Any(b => b.IpCidr == cidr)))
        {
            return Problems.Conflict("AlreadyBanned", "An active ban already covers that target.");
        }

        DateTimeOffset? expires = body.DurationMinutes is { } d ? now.AddMinutes(d) : null;
        long id = queries.InsertBan(player?.Id, cidr, reason!, AdminApi.Actor(context), now, expires);

        // Online targets go now: the banned player's node, and every node whose address is inside the banned network.
        var live = await sessions.GetLiveAsync();
        var toKick = new HashSet<int>();
        if (player is not null && player.Id <= int.MaxValue && live.IsInSession(player.Id))
        {
            toKick.Add((int)player.Id);
        }

        if (cidr is not null)
        {
            foreach (var node in live.Snapshot.Nodes)
            {
                if (node.RemoteAddress is { } text && IPAddress.TryParse(text, out var address) && network.Contains(address))
                {
                    toKick.Add(node.PlayerId);
                }
            }
        }

        foreach (int playerToKick in toKick)
        {
            await sessions.Actor.RemoveNodeAsync(playerToKick, DisconnectCode.Banned, reason!);
        }

        AdminApi.Audit(context, audit, "ban.create", id.ToString(CultureInfo.InvariantCulture), reason, new()
        {
            ["playerId"] = player?.Id.ToString(CultureInfo.InvariantCulture),
            ["ipCidr"] = cidr,
            ["expiresAt"] = expires?.ToString("O", CultureInfo.InvariantCulture),
            ["kicked"] = toKick.Count.ToString(CultureInfo.InvariantCulture),
        });
        return Results.Json(AdminMapping.ToDto(queries.FindBan(id)!, now), ApiJsonContext.Default.BanDto, statusCode: StatusCodes.Status201Created);
    }

    private static IResult RevokeBan(long id, HttpContext context, SqliteAdminQueries queries, AdminStore audit, TimeProvider time)
    {
        if (queries.FindBan(id) is not { } ban)
        {
            return Problems.NotFound("The ban");
        }

        if (!queries.RevokeBan(id, time.GetUtcNow()))
        {
            return Problems.Conflict("AlreadyRevoked", "The ban was already revoked.");
        }

        AdminApi.Audit(context, audit, "ban.revoke", id.ToString(CultureInfo.InvariantCulture), null, new()
        {
            ["playerId"] = ban.PlayerId?.ToString(CultureInfo.InvariantCulture),
            ["ipCidr"] = ban.IpCidr,
        });
        return Results.NoContent();
    }
}

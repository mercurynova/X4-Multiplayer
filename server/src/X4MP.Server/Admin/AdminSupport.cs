using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using X4MP.Core.Relay;
using X4MP.Core.Session;
using X4MP.Persistence;
using X4MP.Protocol;
using X4MP.Server.Api;
using X4MP.Server.Auth;

namespace X4MP.Server.Admin;

/// <summary>Shared helpers of the admin REST endpoints: actor, audit, paging, input checks.</summary>
internal static class AdminApi
{
    public const int MaxReasonLength = 256;

    public static string Actor(HttpContext context) => context.User.Identity?.Name ?? "unknown";

    public static string? RemoteIp(HttpContext context) => context.Connection.RemoteIpAddress?.ToString();

    /// <summary>
    /// Writes an <c>audit_log</c> row (durably, before the response): actor, action, target and the reason. Never pass a secret in
    /// <paramref name="data"/>: passwords, tokens and keys do not belong in the audit log.
    /// </summary>
    public static void Audit(
        HttpContext context, AdminStore store, string action, string? target, string? reason = null, Dictionary<string, string?>? data = null)
    {
        var all = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (data is not null)
        {
            foreach (var (key, value) in data)
            {
                all[key] = value;
            }
        }

        if (reason is not null)
        {
            all["reason"] = reason;
        }

        store.Audit(Actor(context), action, target, RemoteIp(context), all.Count == 0 ? null : all);
    }

    /// <summary>Checks a required, trimmed reason (1 to <see cref="MaxReasonLength"/> characters).</summary>
    public static string? CheckReason(string? reason, Dictionary<string, string[]> errors, bool required = true, string field = "reason")
    {
        string? trimmed = reason?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            if (required)
            {
                errors[field] = ["A reason is required."];
            }

            return null;
        }

        if (trimmed.Length > MaxReasonLength)
        {
            errors[field] = [$"The reason can be at most {MaxReasonLength} characters."];
            return null;
        }

        return trimmed;
    }

    /// <summary>Reads a <c>limit</c> query value: default when absent, an error when outside 1 to <paramref name="max"/>.</summary>
    public static int Limit(int? value, int fallback, int max, Dictionary<string, string[]> errors, string field = "limit")
    {
        if (value is null)
        {
            return fallback;
        }

        if (value < 1 || value > max)
        {
            errors[field] = [$"Use a value from 1 to {max}."];
            return fallback;
        }

        return value.Value;
    }

    public static bool TryParseTime(string? text, out DateTimeOffset value) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value);

    /// <summary>Parses an address or a CIDR block (host bits are masked off) into the canonical <c>base/prefix</c> text.</summary>
    public static bool TryNormalizeCidr(string? text, out string cidr, out IPNetwork network)
    {
        cidr = string.Empty;
        network = default;
        string trimmed = text?.Trim() ?? string.Empty;
        int slash = trimmed.IndexOf('/', StringComparison.Ordinal);
        string addressText = slash < 0 ? trimmed : trimmed[..slash];
        if (!IPAddress.TryParse(addressText, out var address))
        {
            return false;
        }

        int bits = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        int prefix = bits;
        if (slash >= 0 && (!int.TryParse(trimmed[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out prefix) || prefix < 0 || prefix > bits))
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        for (int i = 0; i < bytes.Length; i++)
        {
            int keep = Math.Clamp(prefix - (i * 8), 0, 8);
            bytes[i] &= (byte)(0xFF << (8 - keep));
        }

        network = new IPNetwork(new IPAddress(bytes), prefix);
        cidr = $"{network.BaseAddress}/{prefix}";
        return true;
    }

    public static bool TryParseCidr(string text, out IPNetwork network)
    {
        if (IPNetwork.TryParse(text, out network))
        {
            return true;
        }

        if (IPAddress.TryParse(text, out var single))
        {
            network = new IPNetwork(single, single.AddressFamily == AddressFamily.InterNetwork ? 32 : 128);
            return true;
        }

        return false;
    }
}

/// <summary>Maps persistence rows and actor snapshots to the API DTOs.</summary>
internal static class AdminMapping
{
    public static BanDto ToDto(BanRecord ban, DateTimeOffset now) => new(
        ban.Id, ban.PlayerId, ban.PlayerName, ban.IpCidr, ban.Reason, ban.CreatedBy, ban.CreatedAt, ban.ExpiresAt, ban.IsActive(now));

    public static PlayerLiveDto ToLive(NodeSnapshot node, DateTimeOffset now, LiveSession live)
    {
        var (teamId, teamName) = live.TeamOf(node.PlayerId);
        return new(
            node.PlayerId, node.ConnectionId, node.Name, node.Roles.ToString(), node.Phase.ToString(), node.Connected, node.RemoteAddress,
            node.RttMs, node.Stats?.Fps ?? 0, (long)Math.Max(0, (now - node.JoinedAt).TotalSeconds), live.Muted.Contains(node.PlayerId), teamId, teamName,
            node.Stats is { } s ? new NodeStatsDto(s.Fps, s.FrameMsP95, s.GameTime, s.RttMs, s.RxBytesPerS, s.TxBytesPerS, s.NetMainMsP95, s.MemoryMb, s.ReceivedAt) : null,
            node.Sector == 0 ? null : node.Sector,
            live.SectorName(node.Sector),
            node.Sector == 0 ? null : new Vec3Dto(Quantize.PositionToMetres(node.PosX), Quantize.PositionToMetres(node.PosY), Quantize.PositionToMetres(node.PosZ)),
            node.ShipNetId == 0 ? null : node.ShipNetId);
    }

    public static AuthorityStatusDto? ToDto(AuthoritySnapshot authority) =>
        authority.Status == AuthorityStatus.None
            ? null
            : new AuthorityStatusDto(authority.PlayerId, authority.Name, authority.Status.ToString(), authority.GraceRemainingSeconds, authority.GameBuild, authority.ModVersion);

    public static PlayerDto ToDto(
        PlayerRecord player, bool online, MuteEntry? mute, BanRecord? activeBan, DateTimeOffset now, (long? Id, string? Name) team = default) => new(
        player.Id, player.Name, player.FirstSeen, player.LastSeen, player.TotalSeconds, online, mute is not null, mute?.Until,
        activeBan is null ? null : ToDto(activeBan, now), player.LastIp, player.Notes, team.Id, team.Name);

    public static AuditEntryDto ToDto(AuditRecord row)
    {
        Dictionary<string, string>? data = null;
        string? reason = null;
        if (row.DataJson is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(row.DataJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    data = [];
                    foreach (var property in doc.RootElement.EnumerateObject())
                    {
                        data[property.Name] = property.Value.ValueKind == JsonValueKind.Null ? string.Empty : property.Value.ToString();
                    }

                    reason = data.TryGetValue("reason", out var r) ? r : null;
                }
            }
            catch (JsonException)
            {
                data = null; // not our shape: leave it out rather than fail the listing
            }
        }

        return new AuditEntryDto(row.Id, row.At, row.Actor, row.Action, row.Target, reason, data, row.RemoteIp);
    }
}

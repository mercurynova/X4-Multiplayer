using System.Globalization;
using Serilog.Events;
using X4MP.Core.Relay;
using X4MP.Persistence;
using X4MP.Proto;
using X4MP.Server.Api;
using X4MP.Server.Auth;
using X4MP.Server.Logging;

namespace X4MP.Server.Admin;

/// <summary><c>/api/v1/chat</c> (history, admin message) and <c>/api/v1/logs</c> (ring buffer query, rolling file download).</summary>
internal static class ChatLogEndpoints
{
    public static void Map(IEndpointRouteBuilder routes)
    {
        var chat = routes.MapGroup("/api/v1/chat");
        chat.MapGet("", HistoryAsync).RequireAuthorization(AdminPolicies.Viewer);
        chat.MapPost("", SendAsync).RequireAuthorization(AdminPolicies.Admin);

        var logs = routes.MapGroup("/api/v1/logs");
        logs.MapGet("", QueryLogs).RequireAuthorization(AdminPolicies.Viewer);
        logs.MapGet("/download", DownloadLog).RequireAuthorization(AdminPolicies.Admin);
    }

    // ------------------------------------------------------------------ chat

    private static IResult HistoryAsync(long? sessionId, long? before, int? limit, SqliteAdminQueries queries)
    {
        var errors = new Dictionary<string, string[]>();
        int count = AdminApi.Limit(limit, 100, 500, errors);
        if (errors.Count > 0)
        {
            return Problems.Validation(errors);
        }

        var rows = queries.ChatHistory(sessionId, before, count);
        return Results.Json(
            [.. rows.Select(r => new ChatMessageDto(r.Id, r.At, r.From, r.FromAdmin, r.Channel, r.Text))],
            ApiJsonContext.Default.ListChatMessageDto);
    }

    private static async Task<IResult> SendAsync(
        SendChatRequest? body, HttpContext context, AdminSessions sessions, IChatControl chat, AdminStore audit)
    {
        var result = await ChatSender.SendAsync(body, AdminApi.Actor(context), AdminApi.RemoteIp(context), sessions, chat, audit);
        if (result.Errors is not null)
        {
            return Problems.Validation(result.Errors);
        }

        if (result.ConflictCode is not null)
        {
            return Problems.Conflict(result.ConflictCode, result.ConflictMessage!);
        }

        return Results.Json(new ChatSentDto(result.Delivered), ApiJsonContext.Default.ChatSentDto, statusCode: StatusCodes.Status202Accepted);
    }

    // ------------------------------------------------------------------ logs

    private static IResult QueryLogs(string? level, string? source, string? q, long? before, int? limit, RingBufferSink ring)
    {
        var errors = new Dictionary<string, string[]>();
        LogEventLevel? minimum = null;
        if (!string.IsNullOrWhiteSpace(level))
        {
            if (Enum.TryParse<LogEventLevel>(level.Trim(), ignoreCase: true, out var parsed))
            {
                minimum = parsed;
            }
            else
            {
                errors["level"] = ["Use one of: " + string.Join(", ", Enum.GetNames<LogEventLevel>()) + "."];
            }
        }

        int count = AdminApi.Limit(limit, 200, 1000, errors);
        if (errors.Count > 0)
        {
            return Problems.Validation(errors);
        }

        var (firstSeq, events) = ring.SnapshotWithSequence();
        var matches = new List<LogEntryDto>();
        for (int i = events.Count - 1; i >= 0 && matches.Count < count; i--)
        {
            long seq = firstSeq + i;
            if (before is { } cursor && seq >= cursor)
            {
                continue;
            }

            var entry = events[i];
            if (minimum is { } min && entry.Level < min)
            {
                continue;
            }

            string sourceContext = entry.Properties.TryGetValue("SourceContext", out var sc) ? Unquote(sc) : string.Empty;
            if (!string.IsNullOrWhiteSpace(source) && sourceContext.IndexOf(source.Trim(), StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            string message = entry.RenderMessage(CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(q) && message.IndexOf(q.Trim(), StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            matches.Add(new LogEntryDto(
                seq, entry.Timestamp, entry.Level.ToString(), sourceContext, message, entry.Exception?.ToString(),
                entry.Properties
                    .Where(p => p.Key != "SourceContext")
                    .ToDictionary(p => p.Key, p => Unquote(p.Value), StringComparer.Ordinal)));
        }

        matches.Reverse();
        return Results.Json(matches, ApiJsonContext.Default.ListLogEntryDto);
    }

    private static string Unquote(LogEventPropertyValue value) =>
        value is ScalarValue { Value: string s } ? s : value.ToString();

    private static IResult DownloadLog(string? date, HttpContext context, LoggingOptions options, AdminStore audit)
    {
        DateTime day = DateTime.Now.Date;
        if (!string.IsNullOrWhiteSpace(date)
            && !DateTime.TryParseExact(date.Trim(), ["yyyy-MM-dd", "yyyyMMdd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out day))
        {
            return Problems.Validation("date", "Use yyyy-MM-dd.");
        }

        // The name is built from the parsed date only, so no user text reaches the path.
        string name = options.FileName.Replace(".log", day.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log", StringComparison.Ordinal);
        string path = Path.Combine(options.LogsDir, name);
        if (!File.Exists(path))
        {
            return Problems.NotFound("The log file for that date");
        }

        AdminApi.Audit(context, audit, "logs.download", name);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan | FileOptions.Asynchronous);
        return Results.File(stream, "text/plain; charset=utf-8", fileDownloadName: name);
    }
}

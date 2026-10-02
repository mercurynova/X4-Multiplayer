using System.Globalization;
using X4MP.Proto;
using X4MP.Protocol.Client;

namespace X4MP.FakeNode;

public static partial class LiveRunner
{
    private const string ModNoticeMarker = "differ from this session's list";

    /// <summary>
    /// A bot that verifies its mod outcome (<c>--extensions-preset</c>) was closed during the handshake: compare the <c>ExtensionsMismatch</c> lists with the
    /// expectation and print expected vs actual. A mismatch is counted by <see cref="WriteModsSummaryAsync"/> (the exit code).
    /// </summary>
    private static async Task ReportModRejectionAsync(LiveNodeStats stats, HandshakeRejectedException rejected, SynchronizedWriter lines, string name)
    {
        var expected = stats.ModExpected!;
        string head = $"[{name}] mods: variant={stats.ModLabel} expected={expected.OutcomeName} {expected.Describe()}";
        if (rejected.Code != DisconnectCode.ExtensionsMismatch || rejected.ModViolation is not { } violation)
        {
            stats.ModMatch = false;
            stats.ModDetail = $"rejected with {rejected.Code} (no mod violation): {rejected.Message}";
            await lines.WriteAsync($"{head} actual=rejected {stats.ModDetail} -> MISMATCH").ConfigureAwait(false);
            return;
        }

        string actual = ModExpectation.Describe(violation);
        string links = ModExpectation.Links(violation);
        if (expected.Outcome != ExpectedModOutcome.Reject)
        {
            stats.ModMatch = false;
            stats.ModDetail = $"rejected, expected {expected.OutcomeName}: {actual}";
            await lines.WriteAsync($"{head} actual=rejected {actual} -> MISMATCH").ConfigureAwait(false);
            return;
        }

        bool ok = expected.Matches(violation, out string detail);
        stats.ModMatch = ok;
        stats.ModDetail = ok ? $"rejected {actual}" : $"rejected, lists differ: {detail}";
        await lines.WriteAsync($"{head} actual=rejected {actual}{(links.Length > 0 ? " links=[" + links + "]" : string.Empty)} -> {(ok ? "match" : "MISMATCH (" + detail + ")")}").ConfigureAwait(false);
    }

    /// <summary>
    /// The M1-X5 result: per bot with an expectation, whether the server rejected / warned / admitted as predicted (<c>mods:</c> line with the counts).
    /// Admitted bots are checked here (a Warn admission must have brought the notice, an Admit none). Returns the number of mismatches (they count as errors).
    /// </summary>
    private static async Task<long> WriteModsSummaryAsync(List<LiveNodeStats> stats, SynchronizedWriter lines)
    {
        var bots = stats.Where(s => s.ModExpected is not null).ToList();
        if (bots.Count == 0)
            return 0;

        foreach (var s in bots.Where(s => s.ModAdmitted && s.ModMatch is null))
        {
            var expected = s.ModExpected!;
            var notice = s.Notices.FirstOrDefault(n => n.Contains(ModNoticeMarker, StringComparison.Ordinal));
            string head = $"[{s.Name}] mods: variant={s.ModLabel} expected={expected.OutcomeName} {expected.Describe()}";
            if (expected.Outcome == ExpectedModOutcome.Admit)
            {
                s.ModMatch = notice is null;
                s.ModDetail = notice is null ? "admitted" : "admitted with an unexpected notice: " + notice;
            }
            else
            {
                var missing = expected.Install.Concat(expected.Enable).Concat(expected.Disable).Concat(expected.Update)
                    .Where(m => notice is null || !notice.Contains(m.Name, StringComparison.Ordinal)).Select(m => m.Id).ToList();
                s.ModMatch = notice is not null && missing.Count == 0;
                s.ModDetail = notice is null ? "admitted without the Warn notice"
                    : missing.Count > 0 ? $"admitted, the notice does not name [{string.Join(',', missing)}]: {notice}"
                    : "admitted with notice: " + notice;
            }

            await lines.WriteAsync($"{head} actual={s.ModDetail} -> {(s.ModMatch == true ? "match" : "MISMATCH")}").ConfigureAwait(false);
        }

        long mismatches = 0;
        foreach (var s in bots)
        {
            if (s.ModMatch is null)
            {
                s.ModMatch = false;
                s.ModDetail = s.LastError is null ? "no outcome (the run ended first)" : "connection failed: " + s.LastError;
                if (s.LastError is null)
                    await lines.WriteAsync($"[{s.Name}] mods: variant={s.ModLabel} {s.ModDetail} -> MISMATCH").ConfigureAwait(false);
            }

            if (s.ModMatch == false && !(s.LastError is not null && s.ModDetail.StartsWith("connection failed", StringComparison.Ordinal)))
                mismatches++; // a failed connection is already counted as a node error
        }

        long rejected = bots.Count(s => !s.ModAdmitted && s.ModDetail.StartsWith("rejected", StringComparison.Ordinal));
        long warned = bots.Count(s => s.ModAdmitted && s.ModExpected!.Outcome == ExpectedModOutcome.AdmitWithWarning && s.ModMatch == true);
        await lines.WriteAsync(string.Create(CultureInfo.InvariantCulture,
            $"mods: bots={bots.Count} rejected={rejected} admitted={bots.Count(s => s.ModAdmitted)} warned={warned} " +
            $"expected-rejections={bots.Count(s => s.ModExpected!.Outcome == ExpectedModOutcome.Reject)} matches={bots.Count - mismatches} mismatches={mismatches}")).ConfigureAwait(false);
        return mismatches;
    }
}

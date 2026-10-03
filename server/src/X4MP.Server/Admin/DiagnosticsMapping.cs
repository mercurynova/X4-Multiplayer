using System.Globalization;
using X4MP.Core.Diagnostics;
using X4MP.Server.Api;

namespace X4MP.Server.Admin;

/// <summary>Maps the in-memory node diagnostics to their DTOs (REST and hub push share it).</summary>
internal static class DiagnosticsMapping
{
    public static NodeDiagnosticsDto ToDto(NodeDiagnosticsSnapshot s) => new(
        s.PlayerId,
        s.Player,
        s.SelfTest is null ? null : ToDto(s.SelfTest),
        [.. s.Lines.Select(l => new NodeLogLineDto(l.At, l.Level.ToString(), l.Text))],
        s.LinesReceived,
        s.LinesDropped);

    public static SelfTestDto ToDto(SelfTestTable t) => new(
        t.At, t.Failed > 0 ? SelfTestParser.Fail : SelfTestParser.Pass, t.Passed, t.Failed, t.Warned, t.Skipped,
        [.. t.Rows.Select(r => new SelfTestRowDto(r.Name, r.Result, r.Detail))]);

    internal static string Invariant(long value) => value.ToString(CultureInfo.InvariantCulture);
}

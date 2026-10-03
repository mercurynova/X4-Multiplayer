namespace X4MP.Server.Api;

// DTOs of the node diagnostics (task M2-13): forwarded log lines and the self-test table. Same conventions as AdminDtos.

/// <summary>One line a node forwarded. <c>Level</c> is <c>Debug</c>, <c>Info</c>, <c>Warn</c> or <c>Error</c>.</summary>
[TsContract]
public sealed record NodeLogLineDto(DateTimeOffset At, string Level, string Text);

/// <summary>One self-test check. <c>Result</c> is <c>PASS</c>, <c>FAIL</c>, <c>WARN</c> or <c>SKIP</c>.</summary>
[TsContract]
public sealed record SelfTestRowDto(string Name, string Result, string Detail);

/// <summary>The last complete self-test table of a node (the rows of the node's latest run); <c>Overall</c> is FAIL when any row failed, else PASS (warnings do not fail it).</summary>
[TsContract]
public sealed record SelfTestDto(DateTimeOffset At, string Overall, int Passed, int Failed, int Warned, int Skipped, List<SelfTestRowDto> Rows);

/// <summary>
/// What the server holds about one node's diagnostics (<c>GET /api/v1/players/{id}/diagnostics</c>, hub push <c>NodeDiagnosticsChanged</c>): the last
/// self-test table (null until a node sent one) and the last forwarded log lines, oldest first. Kept in memory: gone after a server restart.
/// </summary>
[TsContract]
public sealed record NodeDiagnosticsDto(long PlayerId, string Player, SelfTestDto? SelfTest, List<NodeLogLineDto> Lines, long LinesReceived, long LinesDropped);

namespace X4MP.Core.Diagnostics;

/// <summary>One row of a node's self-test table.</summary>
/// <param name="Result"><c>PASS</c>, <c>FAIL</c>, <c>WARN</c> or <c>SKIP</c>.</param>
/// <param name="Name">The check, a token without spaces.</param>
/// <param name="Detail">Free text, possibly empty.</param>
public sealed record SelfTestRow(string Result, string Name, string Detail);

/// <summary>A self-test table: the rows of one run, as far as they have arrived.</summary>
public sealed record SelfTestTable(DateTimeOffset At, IReadOnlyList<SelfTestRow> Rows)
{
    public int Passed => Rows.Count(r => r.Result == SelfTestParser.Pass);

    public int Failed => Rows.Count(r => r.Result == SelfTestParser.Fail);

    public int Warned => Rows.Count(r => r.Result == SelfTestParser.Warn);

    public int Skipped => Rows.Count(r => r.Result == SelfTestParser.Skip);
}

/// <summary>What one forwarded log line is, as far as the self-test convention goes (docs/mod-design.md 8.5.1).</summary>
public enum SelfTestLineKind
{
    /// <summary>Not a self-test line.</summary>
    None,

    /// <summary>A <c>SELFTEST begin ...</c> line: the next row starts a new table.</summary>
    Begin,

    /// <summary>A <c>SELFTEST summary: ...</c> (or <c>SELFTEST END</c>) line: the run is over.</summary>
    End,

    /// <summary>A well-formed <c>SELFTEST &lt;RESULT&gt; &lt;check&gt; &lt;detail&gt;</c> row.</summary>
    Row,

    /// <summary>Starts with <c>SELFTEST </c> but is neither (for example <c>SELFTEST forwarded 9 lines</c>); ignored.</summary>
    Other,
}

/// <summary>Parser of the self-test line convention the mod writes (docs/mod-design.md 8.5.1). Pure.</summary>
public static class SelfTestParser
{
    public const string Prefix = "SELFTEST ";
    public const string Pass = "PASS";
    public const string Fail = "FAIL";
    public const string Warn = "WARN";
    public const string Skip = "SKIP";
    public const int MaxRows = 200;
    public const int MaxNameLength = 64;
    public const int MaxDetailLength = 300;

    private static readonly char[] Blanks = [' ', '\t'];

    /// <summary>True for any line that belongs to the self-test convention (and so bypasses the log rate limit).</summary>
    public static bool IsSelfTestLine(string? text) => text is not null && text.StartsWith(Prefix, StringComparison.Ordinal);

    public static SelfTestLineKind Parse(string? text, out SelfTestRow? row)
    {
        row = null;
        if (text is null || !IsSelfTestLine(text))
        {
            return SelfTestLineKind.None;
        }

        string rest = text[Prefix.Length..].Trim();
        int space = rest.IndexOfAny(Blanks);
        string first = space < 0 ? rest : rest[..space];
        string after = space < 0 ? string.Empty : rest[(space + 1)..].TrimStart();

        if (first is Pass or Fail or Warn or Skip)
        {
            int nameEnd = after.IndexOfAny(Blanks);
            string name = nameEnd < 0 ? after : after[..nameEnd];
            if (name.Length is 0 or > MaxNameLength || !name.All(IsNameChar))
            {
                return SelfTestLineKind.Other;
            }

            string detail = nameEnd < 0 ? string.Empty : after[(nameEnd + 1)..].Trim();
            if (detail.Length > MaxDetailLength)
            {
                detail = detail[..MaxDetailLength];
            }

            row = new SelfTestRow(first, name, detail);
            return SelfTestLineKind.Row;
        }

        if (first.Equals("begin", StringComparison.OrdinalIgnoreCase))
        {
            return SelfTestLineKind.Begin;
        }

        if (first.Equals("summary:", StringComparison.OrdinalIgnoreCase) || first.Equals("END", StringComparison.Ordinal))
        {
            return SelfTestLineKind.End;
        }

        return SelfTestLineKind.Other;
    }

    private static bool IsNameChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or ':' or '/' or '-';

    /// <summary>Formats a row the way the mod writes it (result padded to 5, name to 16).</summary>
    public static string Format(SelfTestRow row) => Prefix + row.Result.PadRight(5) + " " + row.Name.PadRight(16) + " " + row.Detail;
}

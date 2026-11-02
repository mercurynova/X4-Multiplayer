using System.Text;

namespace X4MP.Launcher.Core;

/// <summary>One extension entry the launcher rewrote, with the exact text it replaced.</summary>
/// <param name="Id">The extension id the entry belongs to.</param>
/// <param name="OriginalTag">The element as it was before the launcher touched it; null when the launcher added it.</param>
/// <param name="WrittenTag">The element the launcher wrote, kept so a restore can tell its own edit from the player's.</param>
public sealed record ProfileTagEdit(string Id, string? OriginalTag, string WrittenTag);

/// <summary>
/// Edits a profile content.xml by replacing text spans instead of re-serialising the document: every byte the launcher
/// did not have to change stays as it was, which is what makes apply followed by restore byte-identical.
/// </summary>
public static class ProfileContentEditor
{
    private const string Extension = "extension";
    private const string Closed = "</extension";

    /// <summary>
    /// Applies the Enable and Disable lines of a plan. A mod that is not in the plan is not touched; an entry the
    /// profile holds for an extension that is no longer installed is not touched either.
    /// </summary>
    public static (string Text, IReadOnlyList<ProfileTagEdit> Edits) Apply(string text, IReadOnlyList<PlanItem> plan)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(plan);

        var edits = new List<ProfileTagEdit>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string result = text;
        foreach (var item in plan)
        {
            if (item.Action is not (PlanAction.Enable or PlanAction.Disable)) continue;
            if (item.Id.Length == 0 || !seen.Add(item.Id)) continue;
            bool enable = item.Action == PlanAction.Enable;

            var element = FindElement(result, item.Id);
            if (element is null)
            {
                int at = result.IndexOf("</content>", StringComparison.Ordinal);
                if (at < 0) continue;
                string added = Insertion(result, at, item.Id, enable);
                result = result.Insert(at, added);
                edits.Add(new ProfileTagEdit(item.Id, null, added));
                continue;
            }

            string original = result[element.Value.Start..element.Value.End];
            string written = RewriteEnabled(original, enable);
            if (string.Equals(written, original, StringComparison.Ordinal)) continue;
            result = string.Concat(result.AsSpan(0, element.Value.Start), written, result.AsSpan(element.Value.End));
            edits.Add(new ProfileTagEdit(item.Id, original, written));
        }

        return (result, edits);
    }

    /// <summary>
    /// Puts the original text back, but only where the file still holds what the launcher wrote. An entry the player
    /// changed in the game is reported and left alone.
    /// </summary>
    public static (string Text, IReadOnlyList<string> Skipped) Restore(string text, IReadOnlyList<ProfileTagEdit> edits)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(edits);

        var skipped = new List<string>();
        string result = text;
        foreach (var edit in edits)
        {
            if (edit.OriginalTag is null)
            {
                int at = result.IndexOf(edit.WrittenTag, StringComparison.Ordinal);
                if (at < 0)
                {
                    skipped.Add(edit.Id);
                    continue;
                }

                result = result.Remove(at, edit.WrittenTag.Length);
                continue;
            }

            var element = FindElement(result, edit.Id);
            if (element is null)
            {
                skipped.Add(edit.Id);
                continue;
            }

            string current = result[element.Value.Start..element.Value.End];
            if (!string.Equals(current, edit.WrittenTag, StringComparison.Ordinal))
            {
                skipped.Add(edit.Id);
                continue;
            }

            result = string.Concat(result.AsSpan(0, element.Value.Start), edit.OriginalTag, result.AsSpan(element.Value.End));
        }

        return (result, skipped);
    }

    private static string Insertion(string text, int at, string id, bool enable)
    {
        var tag = new StringBuilder();
        if (at > 0 && text[at - 1] != '\n') tag.Append('\n');
        tag.Append("  <extension id=\"");
        tag.Append(Escape(id));
        tag.Append("\" enabled=\"");
        tag.Append(enable ? "true" : "false");
        tag.Append("\"></extension>\n");
        return tag.ToString();
    }

    private static string Escape(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal);

    /// <summary>The whole element: the opening tag, its body and the matching end tag when there is one.</summary>
    private static (int Start, int End)? FindElement(string text, string id)
    {
        foreach (var span in XmlTagReader.ReadSpans(text))
        {
            if (!string.Equals(span.Tag.Name, Extension, StringComparison.Ordinal)) continue;
            if (!string.Equals(span.Tag.Get("id"), id, StringComparison.Ordinal)) continue;

            int end = span.End;
            if (!IsSelfClosing(text, span.Start, span.End))
            {
                int close = text.IndexOf(Closed, span.End, StringComparison.Ordinal);
                if (close >= 0)
                {
                    int gt = text.IndexOf('>', close);
                    if (gt >= 0) end = gt + 1;
                }
            }

            return (span.Start, end);
        }

        return null;
    }

    private static bool IsSelfClosing(string text, int start, int end)
    {
        int index = end - 2;
        while (index > start && char.IsWhiteSpace(text[index])) index--;
        return index > start && text[index] == '/';
    }

    /// <summary>Rewrites the enabled attribute of the opening tag and leaves the rest of the element alone.</summary>
    private static string RewriteEnabled(string element, bool enable)
    {
        int tagEnd = OpeningTagEnd(element);
        if (tagEnd < 0) return element;

        string value = enable ? "true" : "false";
        int index = 1;
        while (index < tagEnd)
        {
            while (index < tagEnd && char.IsWhiteSpace(element[index])) index++;
            if (index >= tagEnd || element[index] == '/') break;

            int keyStart = index;
            while (index < tagEnd && !char.IsWhiteSpace(element[index]) && element[index] != '=') index++;
            string key = element[keyStart..index];
            while (index < tagEnd && char.IsWhiteSpace(element[index])) index++;
            if (index >= tagEnd || element[index] != '=')
            {
                if (key.Length == 0) index++;
                continue;
            }

            index++;
            while (index < tagEnd && char.IsWhiteSpace(element[index])) index++;
            if (index >= tagEnd || element[index] is not ('"' or '\'')) return element;

            char quote = element[index];
            int valueStart = index + 1;
            int valueEnd = element.IndexOf(quote, valueStart);
            if (valueEnd < 0 || valueEnd > tagEnd) return element;
            if (string.Equals(key, "enabled", StringComparison.Ordinal))
            {
                return string.Concat(element.AsSpan(0, valueStart), value, element.AsSpan(valueEnd));
            }

            index = valueEnd + 1;
        }

        int insert = tagEnd;
        if (insert > 0 && element[insert - 1] == '/') insert--;
        return element.Insert(insert, " enabled=\"" + value + "\"");
    }

    /// <summary>The index of the '&gt;' that ends the opening tag, ignoring '&gt;' inside a quoted value.</summary>
    private static int OpeningTagEnd(string element)
    {
        char quote = '\0';
        for (int index = 0; index < element.Length; index++)
        {
            char c = element[index];
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                continue;
            }

            if (c is '"' or '\'') quote = c;
            else if (c == '>') return index;
        }

        return -1;
    }
}

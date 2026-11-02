using System.Text;

namespace X4MP.Launcher.Core;

/// <summary>One tag of the tiny subset of XML that X4's content.xml files use.</summary>
internal sealed record XmlTag(string Name, IReadOnlyDictionary<string, string> Attributes)
{
    public string? Get(string key) => Attributes.TryGetValue(key, out var value) ? value : null;
}

/// <summary>Where a tag sits in the text it was read from: <paramref name="Start"/> is its '&lt;', <paramref name="End"/> is exclusive.</summary>
internal readonly record struct XmlTagSpan(int Start, int End, XmlTag Tag);

/// <summary>
/// A deliberately tolerant reader for the tags of content.xml: comments, processing instructions, doctype
/// declarations and end tags are skipped, only quoted attribute values are accepted, and a malformed tag ends the
/// scan. It mirrors the native reader in mod/native/core/mods/mods.cpp, so the launcher and the game agree on what a
/// broken file means.
/// </summary>
internal static class XmlTagReader
{
    private static readonly (string Entity, char Value)[] Entities =
    [
        ("&amp;", '&'), ("&lt;", '<'), ("&gt;", '>'), ("&quot;", '"'), ("&apos;", '\''),
    ];

    public static IEnumerable<XmlTag> Read(string text) => ReadSpans(text).Select(span => span.Tag);

    /// <summary>
    /// The same scan, but each tag is reported with its position, so a caller can replace exactly one tag and keep
    /// every other byte of the file as it was. <see cref="XmlTagSpan.End"/> is exclusive.
    /// </summary>
    public static IEnumerable<XmlTagSpan> ReadSpans(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        int position = 0;
        while (true)
        {
            int open = text.IndexOf('<', position);
            if (open < 0) yield break;

            if (text.AsSpan(open).StartsWith("<!--", StringComparison.Ordinal))
            {
                int end = text.IndexOf("-->", open + 4, StringComparison.Ordinal);
                if (end < 0) yield break;
                position = end + 3;
                continue;
            }

            if (open + 1 < text.Length && text[open + 1] is '?' or '!' or '/')
            {
                int end = text.IndexOf('>', open);
                if (end < 0) yield break;
                position = end + 1;
                continue;
            }

            var (tag, next) = ReadTag(text, open);
            if (tag is null) yield break;
            position = next;
            yield return new XmlTagSpan(open, next, tag);
        }
    }

    private static (XmlTag? Tag, int Next) ReadTag(string text, int open)
    {
        int index = open + 1;
        var name = new StringBuilder();
        while (index < text.Length && !IsSpace(text[index]) && text[index] is not '>' and not '/') name.Append(text[index++]);

        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        while (index < text.Length)
        {
            while (index < text.Length && (IsSpace(text[index]) || text[index] == '/')) index++;
            if (index >= text.Length) return (null, index);
            if (text[index] == '>') return (new XmlTag(name.ToString(), attributes), index + 1);

            var key = new StringBuilder();
            while (index < text.Length && !IsSpace(text[index]) && text[index] is not '=' and not '>' and not '/') key.Append(text[index++]);
            while (index < text.Length && IsSpace(text[index])) index++;
            if (index < text.Length && text[index] == '=')
            {
                index++;
                while (index < text.Length && IsSpace(text[index])) index++;
                if (index >= text.Length || text[index] is not ('"' or '\'')) return (null, index);
                char quote = text[index++];
                int end = text.IndexOf(quote, index);
                if (end < 0) return (null, index);
                attributes[key.ToString()] = DecodeEntities(text.AsSpan(index, end - index));
                index = end + 1;
            }
            else if (key.Length > 0)
            {
                attributes[key.ToString()] = string.Empty;
            }
            else
            {
                index++;
            }
        }

        return (null, index);
    }

    private static string DecodeEntities(ReadOnlySpan<char> value)
    {
        if (value.IndexOf('&') < 0) return value.ToString();
        var decoded = new StringBuilder(value.Length);
        int index = 0;
        while (index < value.Length)
        {
            bool matched = false;
            if (value[index] == '&')
            {
                foreach (var (entity, character) in Entities)
                {
                    if (value[index..].StartsWith(entity, StringComparison.Ordinal))
                    {
                        decoded.Append(character);
                        index += entity.Length;
                        matched = true;
                        break;
                    }
                }
            }

            if (!matched) decoded.Append(value[index++]);
        }

        return decoded.ToString();
    }

    private static bool IsSpace(char c) => c is ' ' or '\t' or '\r' or '\n';
}

/// <summary>One <c>&lt;dependency id="..." optional="..."/&gt;</c> of a content.xml.</summary>
public sealed record ContentDependency(string Id, bool Optional);

/// <summary>The fields of a content.xml the launcher needs, already parsed and defaulted.</summary>
public sealed record ContentDescription(
    string Id,
    string Name,
    string Version,
    bool EnabledByDefault,
    bool SaveDependent,
    IReadOnlyList<ContentDependency> Dependencies);

/// <summary>Reads the parts of content.xml that decide whether a mod is installed, enabled and save-relevant.</summary>
public static class ContentXml
{
    /// <summary>Parses the first <c>&lt;content&gt;</c> tag and every dependency after it; null when there is no usable content tag.</summary>
    public static ContentDescription? Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        ContentDescription? found = null;
        var dependencies = new List<ContentDependency>();
        foreach (var tag in XmlTagReader.Read(text))
        {
            if (tag.Name == "content" && found is null)
            {
                var id = tag.Get("id");
                if (string.IsNullOrEmpty(id)) return null;
                found = new ContentDescription(
                    id,
                    tag.Get("name") ?? string.Empty,
                    tag.Get("version") ?? string.Empty,
                    Truthy(tag.Get("enabled"), true),
                    Truthy(tag.Get("save"), false),
                    []);
            }
            else if (tag.Name == "dependency" && found is not null)
            {
                var id = tag.Get("id");
                if (!string.IsNullOrEmpty(id)) dependencies.Add(new ContentDependency(id, Truthy(tag.Get("optional"), false)));
            }
        }

        return found is null ? null : found with { Dependencies = dependencies };
    }

    /// <summary>
    /// X4 writes versions as hundredths without a dot ("900" is 9.00). Anything that is not three to nine digits is
    /// returned unchanged, so a hand-written "1.30" survives; the rule matches mod/native/core/mods/mods.cpp.
    /// </summary>
    public static string NormaliseVersion(string? version)
    {
        var value = (version ?? string.Empty).Trim();
        if (value.Length is 0 or > 9) return value;
        foreach (char c in value)
        {
            if (c is < '0' or > '9') return value;
        }

        if (value.Length < 3) return value;
        var digits = value;
        while (digits.Length > 3 && digits[0] == '0') digits = digits[1..];
        return digits.Insert(digits.Length - 2, ".");
    }

    /// <summary>The game's boolean spelling: 1/true/yes and 0/false/no, anything else means the default.</summary>
    internal static bool Truthy(string? value, bool fallback)
    {
        if (value is null) return fallback;
        return value.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" => true,
            "0" or "false" or "no" => false,
            _ => fallback,
        };
    }
}

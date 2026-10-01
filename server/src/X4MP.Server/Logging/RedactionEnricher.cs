using System.Text.RegularExpressions;
using Serilog.Core;
using Serilog.Events;

namespace X4MP.Server.Logging;

/// <summary>
/// Redaction policy: secrets never reach any sink. Runs as an enricher, so console, file and ring
/// buffer all see the redacted event. It masks (a) values of properties whose name looks sensitive
/// (password, token, secret, authorization, cookie, api key, player key), at any nesting depth and
/// as dictionary keys (e.g. header maps), and (b) inline "Authorization: Bearer x" / "password=x"
/// fragments inside string values.
/// </summary>
public sealed partial class RedactionEnricher : ILogEventEnricher
{
    public const string Mask = "***REDACTED***";

    private static readonly string[] SensitiveFragments =
    [
        "authorization", "password", "passwd", "secret", "token", "cookie", "apikey", "playerkey", "credential",
    ];

    public static bool IsSensitiveName(string name)
    {
        var normalized = name.Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);
        foreach (var fragment in SensitiveFragments)
        {
            if (normalized.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return normalized.Equals("pwd", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Scrubs inline secrets from free text.</summary>
    public static string ScrubText(string text)
    {
        if (text.Length == 0)
        {
            return text;
        }
        var scrubbed = AuthorizationFragment().Replace(text, "$1" + Mask);
        scrubbed = SecretFragment().Replace(scrubbed, "$1" + Mask);
        return BearerFragment().Replace(scrubbed, "$1" + Mask);
    }

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        // Snapshot first: AddOrUpdateProperty mutates the dictionary.
        foreach (var property in logEvent.Properties.ToArray())
        {
            var redacted = IsSensitiveName(property.Key) ? new ScalarValue(Mask) : Redact(property.Value);
            if (!ReferenceEquals(redacted, property.Value))
            {
                logEvent.AddOrUpdateProperty(new LogEventProperty(property.Key, redacted));
            }
        }
    }

    private static LogEventPropertyValue Redact(LogEventPropertyValue value)
    {
        switch (value)
        {
            case ScalarValue { Value: string s }:
            {
                var scrubbed = ScrubText(s);
                return scrubbed == s ? value : new ScalarValue(scrubbed);
            }

            case StructureValue structure:
            {
                var changed = false;
                var props = new List<LogEventProperty>();
                foreach (var p in structure.Properties)
                {
                    LogEventPropertyValue next = IsSensitiveName(p.Name) ? new ScalarValue(Mask) : Redact(p.Value);
                    changed |= !ReferenceEquals(next, p.Value);
                    props.Add(new LogEventProperty(p.Name, next));
                }
                return changed ? new StructureValue(props, structure.TypeTag) : value;
            }

            case DictionaryValue dictionary:
            {
                var changed = false;
                var elements = new List<KeyValuePair<ScalarValue, LogEventPropertyValue>>();
                foreach (var (key, element) in dictionary.Elements)
                {
                    LogEventPropertyValue next =
                        key.Value is string name && IsSensitiveName(name) ? new ScalarValue(Mask) : Redact(element);
                    changed |= !ReferenceEquals(next, element);
                    elements.Add(new(key, next));
                }
                return changed ? new DictionaryValue(elements) : value;
            }

            case SequenceValue sequence:
            {
                var changed = false;
                var items = new List<LogEventPropertyValue>();
                foreach (var item in sequence.Elements)
                {
                    var next = Redact(item);
                    changed |= !ReferenceEquals(next, item);
                    items.Add(next);
                }
                return changed ? new SequenceValue(items) : value;
            }

            default:
                return value;
        }
    }

    [GeneratedRegex(@"(?i)(\b(?:proxy-)?authorization\s*[:=]\s*)(?:(?:bearer|basic|negotiate)\s+)?[^\s,;]+")]
    private static partial Regex AuthorizationFragment();

    [GeneratedRegex(@"(?i)(\b[\w-]*(?:password|passwd|pwd|secret|token|api[_-]?key)\s*[:=]\s*)[^\s,;&]+")]
    private static partial Regex SecretFragment();

    [GeneratedRegex(@"(?i)(\bbearer\s+)[A-Za-z0-9._~+/=-]{8,}")]
    private static partial Regex BearerFragment();
}

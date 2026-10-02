using System.Text.Json;
using System.Text.Json.Serialization;
using X4MP.Proto;

namespace X4MP.FakeNode;

/// <summary>
/// Loads the extension report a FakeNode sends in <c>ClientHello.extension_list</c> from a JSON file (<c>--extensions file.json</c>):
/// an array of objects named like the <c>ExtensionInfo</c> fields (case-insensitive; enums by name), for example
/// <c>[{"id":"ego_dlc_split","version":"900","source":"Dlc","enabled":true},{"id":"ws_1234567890","name":"Warehouse Fleets","version":"1.4","source":"Workshop","workshopId":1234567890,"classHint":"Sim"}]</c>.
/// Missing fields are defaults; <c>enabled</c> defaults to true; a <c>workshopId</c> is derived from <c>ws_&lt;n&gt;</c> ids by the server.
/// </summary>
public static class ExtensionListFile
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private sealed class Row
    {
        public string Id { get; set; } = string.Empty;
        public string? Name { get; set; }
        public string? Version { get; set; }
        public ExtensionSource Source { get; set; }
        public bool Enabled { get; set; } = true;
        public bool Egosoft { get; set; }
        public ulong WorkshopId { get; set; }
        public string? ContentHash { get; set; }
        public HashKind HashKind { get; set; }
        public bool HasNativeDll { get; set; }
        public bool ReplacesBasegame { get; set; }
        public bool SaveDependent { get; set; }
        public ExtensionClass ClassHint { get; set; }
        public string? Error { get; set; }
        public string? Warning { get; set; }
    }

    public static List<ExtensionInfoT> Parse(string json)
    {
        var rows = JsonSerializer.Deserialize<List<Row>>(json, Options) ?? throw new JsonException("expected a JSON array of extensions");
        var result = new List<ExtensionInfoT>(rows.Count);
        foreach (var r in rows)
        {
            if (string.IsNullOrWhiteSpace(r.Id))
            {
                throw new JsonException("every extension needs an \"id\"");
            }

            result.Add(new ExtensionInfoT
            {
                Id = r.Id,
                Name = r.Name ?? r.Id,
                Version = r.Version ?? string.Empty,
                Source = r.Source,
                Enabled = r.Enabled,
                Egosoft = r.Egosoft,
                WorkshopId = r.WorkshopId,
                ContentHash = string.IsNullOrEmpty(r.ContentHash) ? [] : [.. Convert.FromHexString(r.ContentHash)],
                HashKind = r.HashKind,
                HasNativeDll = r.HasNativeDll,
                ReplacesBasegame = r.ReplacesBasegame,
                SaveDependent = r.SaveDependent,
                ClassHint = r.ClassHint,
                Error = r.Error ?? string.Empty,
                Warning = r.Warning ?? string.Empty,
                Dependencies = [],
            });
        }

        return result;
    }

    /// <summary>Reads and parses a file; the error is a one-line message for the command line.</summary>
    public static (List<ExtensionInfoT>? List, string? Error) Load(string path)
    {
        try
        {
            return (Parse(File.ReadAllText(path)), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            return (null, $"--extensions {path}: {ex.Message}");
        }
    }
}

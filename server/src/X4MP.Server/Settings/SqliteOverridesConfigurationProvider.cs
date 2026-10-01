using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using X4MP.Core.Settings;

namespace X4MP.Server.Settings;

/// <summary>
/// Configuration provider layered over <c>config_overrides</c> (server-design 2.9). Added last, so a GUI override wins
/// over appsettings, environment and CLI. <see cref="Reload"/> re-reads the table and raises the configuration reload
/// token, so <c>IOptionsMonitor&lt;T&gt;</c> consumers see the change immediately.
/// </summary>
public sealed class SqliteOverridesConfigurationProvider(SettingsOverridesStore store) : ConfigurationProvider, IConfigurationSource
{
    public IConfigurationProvider Build(IConfigurationBuilder builder) => this;

    public override void Load() => Data = Read();

    /// <summary>Re-reads the overrides and notifies <c>IOptionsMonitor</c> consumers.</summary>
    public void Reload()
    {
        Load();
        OnReload();
    }

    private Dictionary<string, string?> Read()
    {
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, valueJson) in store.ReadAll())
        {
            var path = ConfigPathOf(key);
            using var doc = JsonDocument.Parse(valueJson);
            Flatten(path, doc.RootElement, data);
        }

        return data;
    }

    /// <summary><c>Replication.TickRateHz</c> to <c>X4MP:Replication:TickRateHz</c>.</summary>
    public static string ConfigPathOf(string apiKey) => SettingsRegistry.KeyPrefix + apiKey.Replace('.', ':');

    private static void Flatten(string path, JsonElement value, Dictionary<string, string?> into)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                into[path] = value.GetString();
                break;
            case JsonValueKind.Number:
                into[path] = value.GetRawText();
                break;
            case JsonValueKind.True:
                into[path] = "true";
                break;
            case JsonValueKind.False:
                into[path] = "false";
                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in value.EnumerateArray())
                {
                    Flatten(path + ":" + index.ToString(CultureInfo.InvariantCulture), item, into);
                    index++;
                }

                break;
            default:
                break; // null / object: nothing to override
        }
    }
}

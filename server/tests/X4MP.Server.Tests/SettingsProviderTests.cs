using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;
using X4MP.Persistence;
using X4MP.Server.Settings;

namespace X4MP.Server.Tests;

public class SettingsProviderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "x4mp-prov-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteConnectionFactory _factory;
    private readonly SettingsOverridesStore _store;

    public SettingsProviderTests()
    {
        _factory = new SqliteConnectionFactory(new PersistenceOptions { DataDir = _dir });
        _store = new SettingsOverridesStore(_factory, TimeProvider.System);
    }

    [Fact]
    public void LoadsBeforeTheSchemaExistsThenReloadsAndRaisesTheChangeToken()
    {
        var provider = new SqliteOverridesConfigurationProvider(_store);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["X4MP:Replication:TickRateHz"] = "20" })
            .Add(provider)
            .Build();
        Assert.Equal("20", config["X4MP:Replication:TickRateHz"]); // no database yet: empty overrides, no throw

        new MigrationRunner(_factory).Migrate();
        var changed = 0;
        using var registration = ChangeToken.OnChange(config.GetReloadToken, () => changed++);

        _store.Apply(new Dictionary<string, string?>
        {
            ["Replication.TickRateHz"] = "45",
            ["Mods.ModListVisibility"] = "\"AllPlayers\"",
            ["Net.SupportedGameBuilds"] = "[\"a\",\"b\"]",
            ["Net.ModBuildStrict"] = "false",
        }, "tester");
        provider.Reload();

        Assert.Equal(1, changed);
        Assert.Equal("45", config["X4MP:Replication:TickRateHz"]);
        Assert.Equal("AllPlayers", config["X4MP:Mods:ModListVisibility"]);
        Assert.Equal("b", config["X4MP:Net:SupportedGameBuilds:1"]);
        Assert.Equal("false", config["X4MP:Net:ModBuildStrict"]);

        _store.Apply(new Dictionary<string, string?> { ["Replication.TickRateHz"] = null }, "tester");
        provider.Reload();
        Assert.Equal("20", config["X4MP:Replication:TickRateHz"]); // the override is gone: lower layers show again
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

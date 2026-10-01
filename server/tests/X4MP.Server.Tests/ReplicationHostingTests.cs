using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using X4MP.Core.Interest;
using X4MP.Core.Replication;
using X4MP.Core.Session;
using X4MP.Core.Settings;
using X4MP.Core.Teams;
using X4MP.Core.World;

namespace X4MP.Server.Tests;

/// <summary>Replication is wired into the host by one line (<c>AddReplication</c>): module, order, options and settings.</summary>
public class ReplicationHostingTests
{
    [Fact]
    public async Task TheHostRegistersTheModuleAfterTheInterestManagerAndTeamsStayFirst()
    {
        await using var factory = new AuthFactory();
        _ = factory.NewClient();

        var modules = factory.Services.GetServices<ISessionModule>().ToList();
        var replication = factory.Services.GetRequiredService<ReplicationModule>();

        Assert.Same(replication, modules.Single(m => m is ReplicationModule));
        Assert.IsType<TeamModule>(modules[0]);
        int mirror = modules.IndexOf(factory.Services.GetRequiredService<WorldMirror>());
        int interest = modules.IndexOf(factory.Services.GetRequiredService<InterestManager>());
        int index = modules.IndexOf(replication);
        Assert.True(mirror < interest && interest < index, "the module order is mirror, interest, replication");
    }

    [Fact]
    public async Task TheReplicationOptionsAreLiveSettingsWithTheProtocolDefaults()
    {
        await using var factory = new AuthFactory();
        _ = factory.NewClient();

        var registry = factory.Services.GetRequiredService<SettingsRegistry>();
        var options = factory.Services.GetRequiredService<IOptionsMonitor<ReplicationOptions>>().CurrentValue;

        foreach (string key in new[]
        {
            "Replication.TickRateHz", "Replication.BandwidthBudgetKBps", "Replication.MaxFramePayloadBytes", "Replication.KeyframeNearSectorSeconds",
            "Replication.KeyframeAdjacentSeconds", "Replication.ChecksumIntervalSeconds", "Replication.TombstoneSeconds", "Replication.SpawnHoldTicks",
            "Replication.InFlightTimeoutMs", "Replication.ResyncMinIntervalMs",
        })
        {
            Assert.True(registry.TryGet(key, out var descriptor), key);
            Assert.Equal(SettingScope.Live, descriptor.Scope);
        }

        Assert.Equal(20, options.TickRateHz);
        Assert.Equal(256, options.BandwidthBudgetKBps);   // 12.8 KB per tick at 20 Hz (server-design 2.6)
        Assert.Equal(5, options.KeyframeNearSectorSeconds);
        Assert.Equal(15, options.KeyframeAdjacentSeconds);
        Assert.Equal(5, options.ChecksumIntervalSeconds);
        Assert.Equal(5, options.TombstoneSeconds);
    }
}

using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using X4MP.Core.Teams;
using X4MP.Persistence;

namespace X4MP.Server.Tests;

/// <summary>A fresh data dir must start without the team store reading tables that do not exist yet.</summary>
public class TeamsStartupTests
{
    private sealed class SpyStore(ITeamStore inner) : ITeamStore
    {
        public int Loads;
        public List<Exception> Failures { get; } = [];

        public TeamStateSnapshot? LoadLatest()
        {
            Interlocked.Increment(ref Loads);
            try
            {
                return inner.LoadLatest();
            }
            catch (Exception ex)
            {
                Failures.Add(ex);
                throw;
            }
        }

        public bool Save(long sessionId, TeamStateSnapshot snapshot) => inner.Save(sessionId, snapshot);
    }

    [Fact]
    public async Task TheHostSuppliesTheEconomyHalfOfSessionSettingsFromTheLiveOptions()
    {
        await using var factory = new AuthFactory();
        _ = factory.NewClient();

        var module = factory.Services.GetRequiredService<TeamModule>();
        var registry = factory.Services.GetRequiredService<X4MP.Core.Settings.SettingsRegistry>();
        var options = factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<X4MP.Core.Economy.EconomyOptions>>().CurrentValue;

        var economy = module.EconomySource!()!;
        Assert.Equal(options.CreditMode, economy.CreditMode);
        Assert.Equal(options.DonateScope, economy.DonateScope);
        Assert.Equal(options.MaxSingleTransfer, economy.MaxTransferAmount);
        Assert.Equal(X4MP.Proto.EffectiveCreditMode.PerPlayer, economy.EffectiveMode); // Auto with no team yet
        Assert.Equal(X4MP.Proto.EffectiveCreditMode.Shared, X4MP.Server.Teams.TeamsExtensions.BuildEconomySettings(options, 1).EffectiveMode);
        foreach (string key in new[] { "Economy.CreditMode", "Economy.DonateScope", "Economy.MaxSingleTransfer", "Economy.TeamPoolEnabled" })
        {
            Assert.True(registry.TryGet(key, out var descriptor), key);
            Assert.True(descriptor.PushToNodes, key);
        }
    }

    [Fact]
    public async Task AFreshDataDirReadsTheTeamStoreAfterTheMigrationsRan()
    {
        SpyStore? spy = null;
        await using var factory = new AuthFactory();
        using var client = factory.WithWebHostBuilder(b => b.ConfigureServices(services =>
            services.AddSingleton<ITeamStore>(sp => spy = new SpyStore(new X4MP.Persistence.SqliteTeamStore(
                sp.GetRequiredService<X4MP.Persistence.SqliteConnectionFactory>(), sp.GetRequiredService<X4MP.Persistence.PersistenceWriter>()))))).CreateClient();

        Assert.Empty(factory.Services.GetRequiredService<TeamModule>().Teams);
        Assert.NotNull(spy);
        Assert.Equal(1, spy!.Loads); // read once, after DatabaseStartup
        Assert.Empty(spy.Failures);  // "no such table: teams" used to be thrown here (the module loaded in its constructor)
    }
}

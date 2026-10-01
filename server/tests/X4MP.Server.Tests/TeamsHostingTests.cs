using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using X4MP.Core.Session;
using X4MP.Core.Settings;
using X4MP.Core.Teams;

namespace X4MP.Server.Tests;

/// <summary>The Teams module is wired into the host by one line (<c>AddTeams</c>): module, directory, options and settings.</summary>
public class TeamsHostingTests
{
    [Fact]
    public async Task TheHostRegistersTheTeamModuleAsDirectoryAndSessionModuleAndFirstInLine()
    {
        await using var factory = new AuthFactory();
        _ = factory.NewClient();

        var module = factory.Services.GetRequiredService<TeamModule>();

        Assert.Same(module, factory.Services.GetRequiredService<ITeamDirectory>());
        Assert.Same(module, factory.Services.GetServices<ISessionModule>().First());
        Assert.Empty(module.Teams); // a fresh database
        Assert.IsType<X4MP.Persistence.SqliteTeamStore>(factory.Services.GetRequiredService<ITeamStore>());
    }

    [Fact]
    public async Task TheHostPointsTheModulesResyncAtReplication()
    {
        await using var factory = new AuthFactory();
        _ = factory.NewClient();

        var module = factory.Services.GetRequiredService<TeamModule>();

        Assert.NotNull(module.ResyncPlayer);
        Assert.False(module.ResyncPlayer!(12345)); // replication refuses a player that is not in game; nothing throws
    }

    [Fact]
    public async Task TheTeamOptionsAreSettingsWithTheAdr017Defaults()
    {
        await using var factory = new AuthFactory();
        _ = factory.NewClient();

        var registry = factory.Services.GetRequiredService<SettingsRegistry>();
        var options = factory.Services.GetRequiredService<IOptionsMonitor<TeamOptions>>().CurrentValue;

        foreach (string key in new[]
        {
            "Teams.JoinMode", "Teams.AutoAssign", "Teams.AllowCreateInLobby", "Teams.LobbyTimeoutSeconds", "Teams.MaxTeams",
            "Teams.DefaultRelation", "Teams.AssetPolicy", "Teams.AllowFriendlyFire", "Teams.AllowAssetTransfer",
            "Teams.MoveAssetsWithPlayer", "Teams.AllowSelfTeamChange", "Teams.RelationChangePolicy",
        })
        {
            Assert.True(registry.TryGet(key, out var descriptor), key);
            Assert.Equal(SettingScope.Live, descriptor.Scope);
        }

        Assert.Equal(X4MP.Proto.TeamJoinMode.Auto, options.JoinMode);
        Assert.Equal(X4MP.Proto.AutoAssignStrategy.SingleTeam, options.AutoAssign);
        Assert.Equal(300, options.LobbyTimeoutSeconds);
        Assert.Equal(8, options.MaxTeams);
    }
}

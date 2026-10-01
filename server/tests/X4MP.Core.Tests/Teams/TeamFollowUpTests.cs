using X4MP.Core.Session;
using X4MP.Core.Settings;
using X4MP.Core.Teams;
using X4MP.Proto;

namespace X4MP.Core.Tests.Teams;

/// <summary>M1-T3 follow-ups: the economy half of SessionSettings and the preset guard for the authority's player.</summary>
public class TeamFollowUpTests
{
    private static SessionSettingsSnapshot Snapshot(long version) => new(version, new Dictionary<string, System.Text.Json.JsonElement>());

    [Fact]
    public async Task WelcomeCarriesTheEconomySettingsAndALiveChangePushesThemToInGameNodes()
    {
        var economy = new EconomySettingsT { CreditMode = CreditMode.PerPlayer, EffectiveMode = EffectiveCreditMode.PerPlayer, DonateScope = EconomyScope.Teammates, MaxTransferAmount = 1000 };
        await using var rig = new TeamRig(new TeamOptions { AutoAssign = AutoAssignStrategy.NewTeamPerPlayer });
        rig.Teams.EconomySource = () => new EconomySettingsT { CreditMode = economy.CreditMode, EffectiveMode = economy.EffectiveMode, DonateScope = economy.DonateScope, MaxTransferAmount = economy.MaxTransferAmount };

        var first = await rig.JoinAsync("C1");
        await rig.Rig.BringInGameAsync(first);
        var second = await rig.JoinAsync("C2");
        await rig.SettleAsync();

        Assert.NotNull(second.Welcome.Settings.Economy);
        Assert.Equal(CreditMode.PerPlayer, second.Welcome.Settings.Economy.CreditMode);
        Assert.Equal(1000L, second.Welcome.Settings.Economy.MaxTransferAmount);

        int before = first.Connection.SentOf(MsgType.SessionSettings).Count;
        economy.DonateScope = EconomyScope.Allied; // a Live economy setting changed
        economy.MaxTransferAmount = 50;
        await rig.Rig.Actor.PushAsync(Snapshot(2), default);
        await rig.SettleAsync();

        var pushed = first.Connection.SentOf(MsgType.SessionSettings);
        Assert.Equal(before + 1, pushed.Count);
        var settings = pushed[^1].Decode<SessionSettings>().UnPack();
        Assert.Equal(EconomyScope.Allied, settings.Economy.DonateScope);
        Assert.Equal(50L, settings.Economy.MaxTransferAmount);
        Assert.True(settings.Version > 1);

        // Nothing changed: no push.
        await rig.Rig.Actor.PushAsync(Snapshot(3), default);
        await rig.SettleAsync();
        Assert.Equal(before + 1, first.Connection.SentOf(MsgType.SessionSettings).Count);
    }

    [Fact]
    public async Task ApplyingAPresetThatWouldMoveTheAuthoritysPlayerIsRefusedWhileRunning()
    {
        await using var rig = new TeamRig(new TeamOptions { AutoAssign = AutoAssignStrategy.SingleTeam });
        var other = await rig.JoinAsync("C1"); // player 1 leads team 1
        await rig.Rig.BringInGameAsync(other);
        var boss = await rig.JoinAsync("Boss", Role.Authority | Role.Client); // player 2, in team 1
        await rig.Rig.BringInGameAsync(boss);
        await rig.SettleAsync();

        // Not running yet: a preset may do anything (one team per player).
        Assert.True((await rig.Teams.ApplyPresetAsync(TeamPreset.AlliedSeparate)).Ok);
        Assert.NotEqual(rig.Teams.TeamOf(other.PlayerId), rig.Teams.TeamOf(boss.PlayerId));
        Assert.True((await rig.Teams.ApplyPresetAsync(TeamPreset.CoOp)).Ok);
        Assert.Equal(rig.Teams.TeamOf(other.PlayerId), rig.Teams.TeamOf(boss.PlayerId));

        Assert.True((await rig.Rig.Actor.ApplyAsync(SessionTrigger.CheckpointStored)).Applied);
        int team = rig.Teams.TeamOf(boss.PlayerId)!.Value;
        var members = rig.Teams.Snapshot().Members.ToArray();

        var refused = await rig.Teams.ApplyPresetAsync(TeamPreset.FreeForAll);
        Assert.False(refused.Ok);
        Assert.Equal(TeamRejectReason.SessionRunningRestricted, refused.Reason);
        Assert.Equal(team, rig.Teams.TeamOf(boss.PlayerId));
        Assert.Equal(members.Select(m => (m.PlayerId, m.TeamId)), rig.Teams.Snapshot().Members.Select(m => (m.PlayerId, m.TeamId)));

        // A preset that leaves the authority in its team is fine.
        Assert.True((await rig.Teams.ApplyPresetAsync(TeamPreset.CoOp)).Ok);
    }
}

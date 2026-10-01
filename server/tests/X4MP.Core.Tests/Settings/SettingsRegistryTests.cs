using System.Text.Json;
using X4MP.Core.Net;
using X4MP.Core.Settings;

namespace X4MP.Core.Tests.Settings;

public class SettingsRegistryTests
{
    private static readonly SettingsRegistry Registry = new([typeof(NetOptions), typeof(ReplicationOptions), typeof(ModManagementOptions), typeof(AlertOptions)]);

    private static SettingDescriptor Setting(string key)
    {
        Assert.True(Registry.TryGet(key, out var d), key);
        return d;
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    [Fact]
    public void KeysConfigPathsAndScopesComeFromTheAttributes()
    {
        var tick = Setting("Replication.TickRateHz");
        Assert.Equal("X4MP:Replication:TickRateHz", tick.ConfigPath);
        Assert.Equal(SettingScope.Live, tick.Scope);
        Assert.True(tick.PushToNodes);
        Assert.Equal(1, tick.Min);
        Assert.Equal(60, tick.Max);
        Assert.Equal(SettingScope.Boot, Setting("Net.MaxFrameBytes").Scope);
        Assert.True(Setting("Net.JoinPassword").Secret);
        Assert.Equal(["AdminsOnly", "AdminsAndViewers", "AllPlayers"], Setting("Mods.ModListVisibility").EnumValues);
        Assert.False(Registry.TryGet("Net.ServerCaps", out _)); // not marked [Setting]
    }

    [Theory]
    [InlineData("Replication.TickRateHz", "30", true)]
    [InlineData("Replication.TickRateHz", "1", true)]
    [InlineData("Replication.TickRateHz", "60", true)]
    [InlineData("Replication.TickRateHz", "0", false)]
    [InlineData("Replication.TickRateHz", "61", false)]
    [InlineData("Replication.TickRateHz", "2.5", false)]
    [InlineData("Replication.TickRateHz", "\"30\"", false)]
    [InlineData("Replication.TickRateHz", "99999999999", false)]
    [InlineData("Net.ModBuildStrict", "true", true)]
    [InlineData("Net.ModBuildStrict", "1", false)]
    [InlineData("Net.ServerName", "\"hello\"", true)]
    [InlineData("Net.ServerName", "5", false)]
    [InlineData("Mods.ModListVisibility", "\"AllPlayers\"", true)]
    [InlineData("Mods.ModListVisibility", "\"allplayers\"", true)]
    [InlineData("Mods.ModListVisibility", "\"Nobody\"", false)]
    [InlineData("Net.SupportedGameBuilds", "[\"a\",\"b\"]", true)]
    [InlineData("Net.SupportedGameBuilds", "[1]", false)]
    public void ValidateAcceptsOnlyWellTypedInRangeValues(string key, string json, bool valid) =>
        Assert.Equal(valid, SettingsRegistry.Validate(Setting(key), Json(json)).IsValid);

    [Fact]
    public void StringsHonourMaxLengthAndEnumsNormalizeToTheirName()
    {
        var tooLong = SettingsRegistry.Validate(Setting("Net.ServerName"), JsonSerializer.SerializeToElement(new string('x', 65)));
        Assert.Equal("TooLong", tooLong.ErrorCode);
        var normalized = SettingsRegistry.Validate(Setting("Mods.ModListVisibility"), Json("\"adminsandviewers\""));
        Assert.Equal("AdminsAndViewers", normalized.Normalized.GetString());
    }

    [Fact]
    public void ReadValueAndDefaultRenderEnumsAsNames()
    {
        var options = new ModManagementOptions { ModListVisibility = ModListVisibility.AllPlayers };
        Assert.Equal("AllPlayers", SettingsRegistry.ReadValue(Setting("Mods.ModListVisibility"), options).GetString());
        Assert.Equal("AdminsOnly", SettingsRegistry.DefaultValue(Setting("Mods.ModListVisibility")).GetString());
        Assert.Equal(20, SettingsRegistry.DefaultValue(Setting("Replication.TickRateHz")).GetInt32());
    }

    [SettingsSection("X4MP:Dup", "Dup")]
    private sealed class DupA
    {
        [Setting("a")]
        public int Same { get; set; }
    }

    [SettingsSection("X4MP:Dup", "Dup")]
    private sealed class DupB
    {
        [Setting("b")]
        public int Same { get; set; }
    }

    [Fact]
    public void DuplicateKeysAreRejected() =>
        Assert.Throws<InvalidOperationException>(() => new SettingsRegistry([typeof(DupA), typeof(DupB)]));
}

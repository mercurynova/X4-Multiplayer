namespace X4MP.Launcher.Core.Tests;

public class ModPlannerTests
{
    [Fact]
    public void EnablesARequiredModThatIsInstalledAndOff()
    {
        using var tree = new TempTree();

        var plan = ModPlanner.Plan(Manifest(Mod("sim_mod")), [Installed("sim_mod", enabled: false)], Profile(tree));

        var item = Assert.Single(plan);
        Assert.Equal(PlanAction.Enable, item.Action);
        Assert.Equal("Enable", item.ActionText);
    }

    [Fact]
    public void ACorrectModSetProducesNoPlan()
    {
        using var tree = new TempTree();

        var plan = ModPlanner.Plan(Manifest(Mod("sim_mod")), [Installed("sim_mod")], Profile(tree));

        Assert.Empty(plan);
    }

    [Fact]
    public void DisablesABlockedModThatIsOn()
    {
        using var tree = new TempTree();

        var plan = ModPlanner.Plan(
            Manifest(Mod("blocked_mod", rule: "Blocked", enabled: false)),
            [Installed("blocked_mod")],
            Profile(tree));

        var item = Assert.Single(plan);
        Assert.Equal(PlanAction.Disable, item.Action);
    }

    [Fact]
    public void ReportsAMissingModWithItsLinks()
    {
        using var tree = new TempTree();

        var plan = ModPlanner.Plan(
            Manifest(Mod("missing_mod", version: "1.30", ws: 1234567890, nexus: "https://www.nexusmods.com/x4foundations/mods/42")),
            [],
            Profile(tree));

        var item = Assert.Single(plan);
        Assert.Equal(PlanAction.Missing, item.Action);
        Assert.Equal("Install", item.ActionText);
        Assert.Equal(1234567890UL, item.WorkshopId);
        Assert.Equal("https://www.nexusmods.com/x4foundations/mods/42", item.NexusUrl, ignoreCase: true);
        Assert.Equal("1.30", item.VersionText);
        Assert.Null(item.LocalVersion);
    }

    [Fact]
    public void AWorkshopIdIsDerivedFromTheModIdWhenTheManifestHasNone()
    {
        using var tree = new TempTree();

        var plan = ModPlanner.Plan(Manifest(Mod("ws_1234567890")), [], Profile(tree));

        Assert.Equal(1234567890UL, Assert.Single(plan).WorkshopId);
    }

    [Fact]
    public void AVersionMismatchIsReportedAndTheToggleIsStillPlanned()
    {
        using var tree = new TempTree();

        var plan = ModPlanner.Plan(
            Manifest(Mod("sim_mod", version: "1.30")),
            [Installed("sim_mod", version: "1.20", enabled: false)],
            Profile(tree));

        Assert.Equal(2, plan.Count);
        Assert.Equal(PlanAction.Enable, plan[0].Action);
        Assert.Equal(PlanAction.VersionMismatch, plan[1].Action);
        Assert.Equal("Version", plan[1].ActionText);
        Assert.Equal("1.20 -> 1.30", plan[1].VersionText);
    }

    [Theory]
    [InlineData("9.00", "9.0", true)]
    [InlineData("1.30", "1.3", false)]
    [InlineData("1.00", "1", true)]
    [InlineData("1.20", "1.30", false)]
    [InlineData("1.30", "", true)]
    [InlineData(null, "1.30", false)]
    [InlineData("", "", true)]
    public void VersionsMatchTheWayX4WritesThem(string? installed, string? required, bool expected)
    {
        Assert.Equal(expected, ModPlanner.VersionsMatch(installed, required));
    }

    [Fact]
    public void ALocalSimulationModTheServerDoesNotKnowIsDisable()
    {
        using var tree = new TempTree();

        var plan = ModPlanner.Plan(Manifest(), [Installed("loose_sim")], Profile(tree));

        var item = Assert.Single(plan);
        Assert.Equal(PlanAction.Disable, item.Action);
        Assert.Equal("Not in the server's mod set", item.Notes);
        Assert.Equal("loose_sim", item.Id);
    }

    [Fact]
    public void ClientOnlyAndAlwaysOnModsAreLeftAlone()
    {
        using var tree = new TempTree();

        var plan = ModPlanner.Plan(
            Manifest(),
            [Installed("visual_mod", ModClassHint.ClientOnly), Installed("x4mp"), Installed("x4native")],
            Profile(tree));

        Assert.Empty(plan);
    }

    [Fact]
    public void ADisabledLocalSimulationModNeedsNoLine()
    {
        using var tree = new TempTree();

        var plan = ModPlanner.Plan(Manifest(), [Installed("loose_sim", enabled: false)], Profile(tree));

        Assert.Empty(plan);
    }

    [Fact]
    public void TheProfileOverrideWinsOverTheExtensionDefault()
    {
        using var tree = new TempTree();

        var enabled = ModPlanner.Plan(
            Manifest(Mod("sim_mod")),
            [Installed("sim_mod")],
            Profile(tree, ("sim_mod", false)));

        Assert.Equal(PlanAction.Enable, Assert.Single(enabled).Action);

        var alreadyOn = ModPlanner.Plan(
            Manifest(Mod("sim_mod")),
            [Installed("sim_mod", enabled: false)],
            Profile(tree, ("sim_mod", true)));

        Assert.Empty(alreadyOn);
    }

    [Fact]
    public void AnAllowedModIsOnlyEnabledWhenTheServerMarksItOn()
    {
        using var tree = new TempTree();

        var off = ModPlanner.Plan(
            Manifest(Mod("allowed_mod", rule: "Allowed", enabled: false)),
            [Installed("allowed_mod")],
            Profile(tree));

        Assert.Equal(PlanAction.Disable, Assert.Single(off).Action);

        var on = ModPlanner.Plan(
            Manifest(Mod("allowed_mod", rule: "Allowed", enabled: true)),
            [Installed("allowed_mod", enabled: false)],
            Profile(tree));

        Assert.Equal(PlanAction.Enable, Assert.Single(on).Action);
    }

    [Fact]
    public void AFolderWithAnErrorIsNotPlanned()
    {
        using var tree = new TempTree();

        var plan = ModPlanner.Plan(Manifest(), [Installed("broken", error: "content.xml missing or unreadable")], Profile(tree));

        Assert.Empty(plan);
    }

    private static JoinManifest Manifest(params JoinMod[] mods) =>
        new("Friday Run", 7, "900-611726", "AllowClientOnly", [.. mods]);

    private static JoinMod Mod(
        string id,
        string rule = "Required",
        bool enabled = true,
        string cls = "Sim",
        string version = "",
        long ws = 0,
        string? nexus = null) =>
        new(id, id, rule, enabled, cls, version, ws, nexus, string.Empty);

    private static LocalExtension Installed(
        string id,
        ModClassHint cls = ModClassHint.Sim,
        string version = "1.00",
        bool enabled = true,
        ulong workshopId = 0,
        string? error = null) =>
        new(id, ModSource.User, id, id, version, enabled, false, workshopId, [], cls, false, false, error, null);

    private static ProfileContent Profile(TempTree tree, params (string Id, bool Enabled)[] entries)
    {
        var path = Path.Combine(tree.Root, "profile-content.xml");
        if (entries.Length == 0) return ProfileContent.Load(path);

        var body = string.Concat(entries.Select(entry =>
            entry.Enabled
                ? $"<extension id=\"{entry.Id}\" enabled=\"true\"/>"
                : $"<extension id=\"{entry.Id}\" enabled=\"false\"/>"));
        File.WriteAllText(path, "<content>" + body + "</content>");
        return ProfileContent.Load(path);
    }
}

using System.Globalization;

namespace X4MP.Launcher.Core.Tests;

public class ExtensionScannerTests
{
    [Fact]
    public void ReadsEverySourceAndClassifiesIt()
    {
        using var tree = new TempTree();
        var game = tree.Dir("game");
        var documents = tree.Dir("docs");
        var workshop = tree.Dir("workshop", "content", "392160");

        tree.Write("game/extensions/ego_dlc_split/content.xml", Content("ego_dlc_split", "Split Vendetta", "900"));
        tree.Write("game/extensions/ui_mod/content.xml", Content("ui_mod", "UI Mod", "100"));
        tree.Touch("game/extensions/ui_mod/ui/panel.lua");
        tree.Write("game/extensions/sim_mod/content.xml", Content("sim_mod", "Sim Mod", "120", save: true));
        tree.Write("docs/Egosoft/X4/extensions/user_mod/content.xml", Content("user_mod", "User Mod", "105"));
        tree.Touch("docs/Egosoft/X4/extensions/user_mod/t/0001.xml");
        tree.Write(
            "docs/Egosoft/X4/76561198000000000/extensions/profile_mod/content.xml",
            Content("profile_mod", "Profile Mod", "130"));
        tree.Touch("docs/Egosoft/X4/76561198000000000/extensions/profile_mod/md/job.xml");
        tree.Write("workshop/content/392160/1234567890/content.xml", Content("ws_1234567890", "Workshop Mod", "140"));

        var scan = ExtensionScanner.Scan(new GameInstall(game, [workshop]), documents);
        var byId = scan.Where(extension => extension.Id.Length > 0).ToDictionary(extension => extension.Id, StringComparer.Ordinal);

        Assert.Equal(ModSource.Dlc, byId["ego_dlc_split"].Source);
        Assert.Equal(ModClassHint.Dlc, byId["ego_dlc_split"].Class);
        Assert.Equal("9.00", byId["ego_dlc_split"].Version);

        Assert.Equal(ModClassHint.ClientOnly, byId["ui_mod"].Class);
        Assert.Equal("1.00", byId["ui_mod"].Version);

        Assert.Equal(ModClassHint.Sim, byId["sim_mod"].Class);
        Assert.True(byId["sim_mod"].SaveDependent);

        Assert.Equal(ModSource.User, byId["user_mod"].Source);
        Assert.Equal(ModSource.User, byId["profile_mod"].Source);
        Assert.Equal(ModClassHint.Sim, byId["profile_mod"].Class);

        Assert.Equal(ModSource.Workshop, byId["ws_1234567890"].Source);
        Assert.Equal(1234567890UL, byId["ws_1234567890"].WorkshopId);
        Assert.Null(byId["ws_1234567890"].Warning);
    }

    [Fact]
    public void ReadsTheTopLevelFoldersOutOfACatIndex()
    {
        using var tree = new TempTree();
        var game = tree.Dir("game");
        tree.Write("game/extensions/cat_mod/content.xml", Content("cat_mod", "Cat Mod", "100"));
        tree.Write(
            "game/extensions/cat_mod/ext_01.cat",
            "md/jobs.xml 1234 1700000000 0123456789abcdef0123456789abcdef\nlibraries/lib.xml 10 1 0123\n");

        var scan = ExtensionScanner.Scan(new GameInstall(game, []), tree.Dir("docs"));
        var mod = Assert.Single(scan);

        Assert.Equal(ModClassHint.Sim, mod.Class);
    }

    [Fact]
    public void ASubstitutedCatReplacesGameFilesAndAFolderOfUiFilesIsClientOnly()
    {
        using var tree = new TempTree();
        var game = tree.Dir("game");
        tree.Write("game/extensions/subst_mod/content.xml", Content("subst_mod", "Subst Mod", "100"));
        tree.Write("game/extensions/subst_mod/subst_01.cat", "ui/x.xml 1 1 00\n");

        var scan = ExtensionScanner.Scan(new GameInstall(game, []), tree.Dir("docs"));
        var mod = Assert.Single(scan);

        Assert.True(mod.ReplacesBaseGame);
        Assert.False(mod.HasNativeDll);
        Assert.Equal(ModClassHint.ClientOnly, mod.Class);
    }

    [Fact]
    public void ANativeLibraryMakesAModSimulationRelevant()
    {
        using var tree = new TempTree();
        var game = tree.Dir("game");
        tree.Write("game/extensions/native_mod/content.xml", Content("native_mod", "Native Mod", "100"));
        tree.Write("game/extensions/native_mod/x4native.json", """{"library":"native/x4mp.dll"}""");

        var scan = ExtensionScanner.Scan(new GameInstall(game, []), tree.Dir("docs"));
        var mod = Assert.Single(scan);

        Assert.True(mod.HasNativeDll);
        Assert.Equal(ModClassHint.Sim, mod.Class);
    }

    [Fact]
    public void AnyDllAnywhereIsReported()
    {
        using var tree = new TempTree();
        var game = tree.Dir("game");
        tree.Write("game/extensions/dll_mod/content.xml", Content("dll_mod", "Dll Mod", "100"));
        tree.Touch("game/extensions/dll_mod/libs/thing.dll");

        var scan = ExtensionScanner.Scan(new GameInstall(game, []), tree.Dir("docs"));

        Assert.True(Assert.Single(scan).HasNativeDll);
    }

    [Fact]
    public void AWorkshopFolderNameThatDisagreesWithTheIdIsAWarning()
    {
        using var tree = new TempTree();
        var game = tree.Dir("game");
        var workshop = tree.Dir("workshop", "content", "392160");
        tree.Write("workshop/content/392160/999/content.xml", Content("other_mod", "Other Mod", "100"));

        var scan = ExtensionScanner.Scan(new GameInstall(game, [workshop]), tree.Dir("docs"));
        var mod = Assert.Single(scan);

        Assert.Equal("other_mod", mod.Id);
        Assert.Equal(999UL, mod.WorkshopId);
        Assert.NotNull(mod.Warning);
        Assert.Contains("does not match", mod.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public void ADuplicateIdKeepsTheFirstCopyAndWarnsOnTheLaterOne()
    {
        using var tree = new TempTree();
        var game = tree.Dir("game");
        var documents = tree.Dir("docs");
        tree.Write("game/extensions/dup_mod/content.xml", Content("dup_mod", "Install Copy", "100"));
        tree.Write("docs/Egosoft/X4/extensions/dup_mod/content.xml", Content("dup_mod", "User Copy", "200"));

        var scan = ExtensionScanner.Scan(new GameInstall(game, []), documents);
        var copies = scan.Where(extension => extension.Id == "dup_mod").ToArray();

        Assert.Equal(2, copies.Length);
        Assert.Equal(ModSource.Install, copies[0].Source);
        Assert.Null(copies[0].Warning);
        Assert.Equal(ModSource.User, copies[1].Source);
        Assert.NotNull(copies[1].Warning);
        Assert.Contains("duplicate extension id", copies[1].Warning, StringComparison.Ordinal);
    }

    [Fact]
    public void AFolderWithoutAReadableContentXmlIsReportedNotSkipped()
    {
        using var tree = new TempTree();
        var game = tree.Dir("game");
        tree.Dir("game/extensions/empty_mod");
        tree.Write("game/extensions/no_id/content.xml", """<content name="no id"/>""");

        var scan = ExtensionScanner.Scan(new GameInstall(game, []), tree.Dir("docs"));

        Assert.Equal(2, scan.Count);
        Assert.All(scan, extension => Assert.Equal(string.Empty, extension.Id));
        Assert.Contains(scan, extension => extension.Error == "content.xml missing or unreadable");
        Assert.Contains(scan, extension => extension.Error is not null && extension.Error.Contains("no <content id", StringComparison.Ordinal));
    }

    [Fact]
    public void FindsTheGameThroughLibraryFolders()
    {
        using var tree = new TempTree();
        var steam = tree.Dir("steam");
        var library = tree.Dir("library");
        tree.Touch("library/steamapps/common/X4 Foundations/X4.exe");
        tree.Dir("library/steamapps/workshop/content/392160");
        tree.Write(
            "steam/steamapps/libraryfolders.vdf",
            "\"libraryfolders\"\n{\n  \"0\"\n  {\n    \"path\" \"" + library.Replace("\\", "\\\\", StringComparison.Ordinal) + "\"\n  }\n}\n");

        var install = X4InstallFinder.Find([steam]);

        Assert.NotNull(install);
        Assert.EndsWith(Path.Combine("common", "X4 Foundations"), install.GameDir, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            Path.Combine(library, "steamapps", "workshop", "content", "392160"),
            install.WorkshopRoots,
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void LibraryFolderPathsLoseTheVdfEscaping()
    {
        var paths = SteamLibraryFolders.Parse("\"libraryfolders\"\n{\n  \"1\"\n  {\n    \"path\" \"D:\\\\Games\"\n  }\n}\n");

        Assert.Equal(["D:\\Games"], paths);
    }

    [Fact]
    public void ASteamRootWithoutTheGameFindsNothing()
    {
        using var tree = new TempTree();

        Assert.Null(X4InstallFinder.Find([tree.Dir("steam")]));
    }

    [Fact]
    public void FindsTheDirectlyInstalledGameToo()
    {
        using var tree = new TempTree();
        var game = tree.Dir("elsewhere", "X4 Foundations");
        tree.Touch("elsewhere/X4 Foundations/X4.exe");

        var install = X4InstallFinder.Find([], [game]);

        Assert.NotNull(install);
        Assert.Equal(game, install.GameDir, ignoreCase: true);
        Assert.Empty(install.WorkshopRoots);
    }

    private static string Content(string id, string name, string version, bool? enabled = null, bool save = false)
    {
        var attributes = string.Create(
            CultureInfo.InvariantCulture,
            $"id=\"{id}\" name=\"{name}\" version=\"{version}\"");
        if (enabled is not null) attributes += enabled.Value ? " enabled=\"1\"" : " enabled=\"0\"";
        if (save) attributes += " save=\"1\"";
        return "<content " + attributes + "></content>";
    }
}

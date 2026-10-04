using System.IO.Compression;
using System.Text;
using System.Text.Json;
using X4MP.Proto;

namespace X4MP.SaveScan.Tests;

public sealed class SaveScanTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "x4mp-savescan-" + Guid.NewGuid().ToString("N"));

    public SaveScanTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    /// <summary>gzips a fixture the way X4 writes a save (.xml.gz).</summary>
    private string Gz(string fixture)
    {
        string path = Path.Combine(_dir, Path.GetFileNameWithoutExtension(fixture) + ".xml.gz");
        using var file = File.Create(path);
        using var gzip = new GZipStream(file, CompressionLevel.Optimal);
        gzip.Write(File.ReadAllBytes(Fixture(fixture)));
        return path;
    }

    private string WriteGz(string name, string xml)
    {
        string path = Path.Combine(_dir, name);
        using var file = File.Create(path);
        using var gzip = new GZipStream(file, CompressionLevel.Optimal);
        gzip.Write(Encoding.UTF8.GetBytes(xml));
        return path;
    }

    private string Manifest(params (string Idcode, EntityOrigin Origin)[] entries)
    {
        var m = new ManifestT
        {
            GameTime = 12.5,
            NextNetId = 100,
            Entries = [.. entries.Select((e, i) => new ManifestEntryT { NetId = (uint)(i + 1), Idcode = e.Idcode, Origin = e.Origin, ControllerPlayer = 1 })],
        };
        string path = Path.Combine(_dir, "x4mp_test.x4mf");
        File.WriteAllBytes(path, m.SerializeToBinary());
        return path;
    }

    private static (int Code, string Out, string Err) Run(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        int code = SaveScanCli.Run(args, o, e);
        return (code, o.ToString(), e.ToString());
    }

    [Fact]
    public void AuthorityCheckpointWithItsAvatarsIsClean()
    {
        string save = Gz("authority-checkpoint.xml");
        var expect = new ScanExpectations { AvatarOwner = AvatarOwner.Team };
        expect.AvatarIdcodes.Add("AVA-001");
        expect.AvatarIdcodes.Add("AVA-002");
        var r = SaveScanner.ScanFile(save, expect);

        Assert.True(r.Gzip);
        Assert.True(r.Summary.Ok);
        Assert.Equal(2, r.Summary.MpNamed);
        Assert.Equal(2, r.Summary.TeamOwned);
        Assert.Equal(2, r.Summary.ExpectedAvatars);
        Assert.Equal(0, r.Summary.Unexpected);
        Assert.Equal("611726", r.Game.Build);
        Assert.Equal(["AVA-001", "AVA-002"], r.Objects.Select(o => o.Idcode).Order().ToArray());
        var alice = Assert.Single(r.Objects, o => o.Idcode == "AVA-001");
        Assert.Equal("[MP] Alice", alice.Name);
        Assert.Equal("x4mp_team_1", alice.Owner);
        Assert.Equal("ship_fixture_fighter_macro", alice.Macro);
        Assert.Equal("cluster_fixture_01_sector001_macro", alice.Sector);  // the sector it sits in, not the previous one
        Assert.Equal("cluster_fixture_01_sector002_macro", Assert.Single(r.Objects, o => o.Idcode == "AVA-002").Sector);
        // traces: the team factions (with their active flag) and our cue with its variables; the foreign cue is not listed
        Assert.Equal(["x4mp_team_1", "x4mp_team_2", "x4mp_team_3"], r.TeamFactions.Select(f => f.Id).ToArray());
        Assert.Equal("0", r.TeamFactions.Single(f => f.Id == "x4mp_team_3").Active);
        var cue = Assert.Single(r.Cues);
        Assert.Equal("X4MP Galaxy", cue.Name);
        Assert.Equal(["$collected=1", "$sectorcount=2"], cue.Variables.ToArray());
    }

    [Fact]
    public void WithoutExpectationsEveryAvatarIsALeftover()
    {
        var r = SaveScanner.ScanFile(Gz("authority-checkpoint.xml"), new ScanExpectations());
        Assert.False(r.Summary.Ok);
        Assert.Equal(2, r.Summary.Unexpected);
        Assert.All(r.Objects, o => Assert.False(o.Expected));
    }

    [Fact]
    public void ClientQuicksaveFindsGhostsOwnCopyAndReferenceTraces()
    {
        var expect = new ScanExpectations { AvatarOwner = AvatarOwner.Player };
        expect.AvatarIdcodes.Add("AVA-001");  // the player's own avatar copy: allowed
        var r = SaveScanner.ScanFile(Gz("client-quicksave.xml"), expect);

        Assert.False(r.Summary.Ok);
        Assert.Equal(2, r.Summary.MpNamed);      // [MP] Pia (own copy), [MP] Bob (ghost)
        Assert.Equal(2, r.Summary.TeamOwned);    // Bob (team 1), Plain team ship (team 2)
        Assert.Equal(1, r.Summary.ExpectedAvatars);
        var leftovers = r.Objects.Where(o => !o.Expected).Select(o => o.Idcode).Order().ToArray();
        Assert.Equal(["GHO-777", "REF-001", "TEA-002"], leftovers);
        Assert.Contains(ScanKinds.Reference, r.Objects.Single(o => o.Idcode == "REF-001").Kinds);
        // the reference mod's faction counts as a leftover too
        Assert.True(r.Summary.ReferenceLeftovers >= 2);
        Assert.DoesNotContain(r.TeamFactions, f => f.Id.StartsWith("x4mp_client_", StringComparison.Ordinal));
        Assert.Equal(["x4mp_client_7"], r.ReferenceFactions.Select(f => f.Id).ToArray());
    }

    [Fact]
    public void AllowTeamOwnedToleratesTeamShipsWithoutThePrefixOnly()
    {
        var expect = new ScanExpectations { AllowTeamOwned = true };
        expect.AvatarIdcodes.Add("AVA-001");
        var r = SaveScanner.ScanFile(Gz("client-quicksave.xml"), expect);
        // TEA-002 is tolerated; the ghost and the reference ship still are not
        Assert.Equal(3, r.Summary.Unexpected);  // GHO-777, REF-001 and the x4mp_client_7 faction
        Assert.False(r.Summary.Ok);
    }

    [Fact]
    public void CleanSaveIsOk()
    {
        var r = SaveScanner.ScanFile(Gz("clean.xml"), new ScanExpectations());
        Assert.True(r.Summary.Ok);
        Assert.Empty(r.Objects);
        Assert.Empty(r.TeamFactions);
        Assert.Equal(5, r.Summary.ObjectsScanned);  // galaxy, cluster, sector, ship, station
    }

    [Fact]
    public void MissingAvatarAndWrongOwnerAreProblems()
    {
        var expect = new ScanExpectations { AvatarOwner = AvatarOwner.Team };
        expect.AvatarIdcodes.Add("AVA-001");
        expect.AvatarIdcodes.Add("AVA-404");
        var r = SaveScanner.ScanFile(Gz("client-quicksave.xml"), expect);  // AVA-001 is player-owned here
        Assert.Contains(r.Problems, p => p.Contains("AVA-404", StringComparison.Ordinal) && p.Contains("not in the save", StringComparison.Ordinal));
        Assert.Contains(r.Problems, p => p.Contains("AVA-001", StringComparison.Ordinal) && p.Contains("expected a team faction", StringComparison.Ordinal));

        expect.RequireAvatars = false;
        r = SaveScanner.ScanFile(Gz("client-quicksave.xml"), expect);
        Assert.DoesNotContain(r.Problems, p => p.Contains("AVA-404", StringComparison.Ordinal));
    }

    [Fact]
    public void PlainXmlIsAcceptedToo()
    {
        var r = SaveScanner.ScanFile(Fixture("authority-checkpoint.xml"), new ScanExpectations());
        Assert.False(r.Gzip);
        Assert.Equal(2, r.Summary.MpNamed);
    }

    [Fact]
    public void StreamsALargeSaveWithoutBuildingATree()
    {
        var sb = new StringBuilder("<?xml version=\"1.0\"?><savegame><info><game version=\"900\" build=\"1\"/></info><universe>");
        sb.Append("<component class=\"sector\" macro=\"s_macro\">");
        for (int i = 0; i < 20000; i++)
        {
            sb.Append("<component class=\"ship_s\" macro=\"m\" code=\"C").Append(i).Append("\" owner=\"argon\" name=\"N").Append(i).Append("\"/>");
        }

        sb.Append("<component class=\"ship_s\" macro=\"m\" code=\"Z-1\" owner=\"x4mp_team_4\" name=\"[MP] Late\"/></component></universe></savegame>");
        var r = SaveScanner.ScanFile(WriteGz("big.xml.gz", sb.ToString()), new ScanExpectations());
        Assert.Equal(20002, r.Summary.ObjectsScanned);
        var late = Assert.Single(r.Objects);
        Assert.Equal("s_macro", late.Sector);
    }

    [Fact]
    public void NotASaveIsAnErrorNotALeftoverReport()
    {
        string bad = WriteGz("html.xml.gz", "<?xml version=\"1.0\"?><html><body/></html>");
        var (code, _, err) = Run(bad);
        Assert.Equal(SaveScanCli.ExitError, code);
        Assert.Contains("not an X4 save", err, StringComparison.Ordinal);

        string text = Path.Combine(_dir, "text.xml.gz");
        File.WriteAllText(text, "this is not xml at all");
        Assert.Equal(SaveScanCli.ExitError, Run(text).Code);
        Assert.Equal(SaveScanCli.ExitError, Run(Path.Combine(_dir, "missing.xml.gz")).Code);
    }

    [Fact]
    public void CliExitCodesFollowTheLeftovers()
    {
        Assert.Equal(SaveScanCli.ExitOk, Run(Gz("clean.xml"), "--quiet").Code);
        Assert.Equal(SaveScanCli.ExitLeftovers, Run(Gz("client-quicksave.xml"), "--quiet").Code);
        Assert.Equal(SaveScanCli.ExitLeftovers, Run(Gz("authority-checkpoint.xml"), "--quiet").Code);  // nothing said the avatars are expected
        Assert.Equal(SaveScanCli.ExitOk, Run(Gz("authority-checkpoint.xml"), "--expect-avatar", "AVA-001", "--expect-avatar", "ava-002", "--avatar-owner", "team", "--quiet").Code);
        Assert.Equal(SaveScanCli.ExitError, Run().Code);
        Assert.Equal(SaveScanCli.ExitError, Run(Gz("clean.xml"), "--bogus").Code);
        Assert.Equal(SaveScanCli.ExitError, Run(Gz("clean.xml"), "--avatar-owner", "nobody").Code);
        Assert.Equal(SaveScanCli.ExitOk, Run("--help").Code);
    }

    [Fact]
    public void ManifestAvatarsAreTheExpectedOnes()
    {
        string manifest = Manifest(("AVA-001", EntityOrigin.PlayerShip), ("AVA-002", EntityOrigin.PlayerShip), ("STA-001", EntityOrigin.Manifest));
        Assert.Equal(["AVA-001", "AVA-002"], SaveScanCli.ManifestAvatars(manifest));
        Assert.Equal(SaveScanCli.ExitOk, Run(Gz("authority-checkpoint.xml"), "--manifest", manifest, "--avatar-owner", "team", "--quiet").Code);

        // the manifest names an avatar the save does not have
        string manifest2 = Manifest(("AVA-001", EntityOrigin.PlayerShip), ("AVA-002", EntityOrigin.PlayerShip), ("AVA-003", EntityOrigin.PlayerShip));
        var (code, stdout, _) = Run(Gz("authority-checkpoint.xml"), "--manifest", manifest2);
        Assert.Equal(SaveScanCli.ExitLeftovers, code);
        Assert.Contains("AVA-003 is not in the save", stdout, StringComparison.Ordinal);

        string notManifest = Path.Combine(_dir, "junk.x4mf");
        File.WriteAllText(notManifest, "not a manifest, long enough though");
        Assert.Equal(SaveScanCli.ExitError, Run(Gz("clean.xml"), "--manifest", notManifest).Code);
    }

    [Fact]
    public void AvatarsFileListsExpectedIdcodes()
    {
        string list = Path.Combine(_dir, "avatars.txt");
        File.WriteAllText(list, "# the avatars\nAVA-001\n\n  AVA-002  \n");
        Assert.Equal(SaveScanCli.ExitOk, Run(Gz("authority-checkpoint.xml"), "--expect-avatars-file", list, "--quiet").Code);
    }

    [Fact]
    public void JsonReportHasSnakeCaseFieldsAndTheSummary()
    {
        string json = Path.Combine(_dir, "report.json");
        var (code, stdout, _) = Run(Gz("client-quicksave.xml"), "--expect-avatar", "AVA-001", "--json", json);
        Assert.Equal(SaveScanCli.ExitLeftovers, code);
        Assert.Contains("LEFTOVER", stdout, StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(File.ReadAllText(json));
        var root = doc.RootElement;
        Assert.Equal("client-quicksave.xml.gz", root.GetProperty("file").GetString());
        Assert.True(root.GetProperty("gzip").GetBoolean());
        Assert.False(root.GetProperty("summary").GetProperty("ok").GetBoolean());
        Assert.Equal(1, root.GetProperty("summary").GetProperty("expected_avatars").GetInt32());
        var first = root.GetProperty("objects")[0];
        Assert.True(first.TryGetProperty("idcode", out _));
        Assert.True(first.TryGetProperty("kinds", out _));

        // '-' writes the JSON to stdout and the human summary to stderr
        var (_, o2, e2) = Run(Gz("clean.xml"), "--json", "-");
        using var doc2 = JsonDocument.Parse(o2);
        Assert.True(doc2.RootElement.GetProperty("summary").GetProperty("ok").GetBoolean());
        Assert.Contains("result: OK", e2, StringComparison.Ordinal);
    }
}

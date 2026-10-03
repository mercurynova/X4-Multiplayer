using System.IO.Compression;
using System.Text;
using X4MP.Core.Saves;

namespace X4MP.Core.Tests.Saves;

/// <summary>The <c>&lt;patches&gt;</c> reader on synthetic saves (never real game data).</summary>
public sealed class SavePatchesReaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "x4mp-patches-" + Guid.NewGuid().ToString("N"));

    public SavePatchesReaderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Write(string name, string xml, bool gzip = true)
    {
        string path = Path.Combine(_dir, name);
        if (gzip)
        {
            using var file = new FileStream(path, FileMode.Create);
            using var zip = new GZipStream(file, CompressionLevel.Optimal);
            zip.Write(Encoding.UTF8.GetBytes(xml));
        }
        else
        {
            File.WriteAllText(path, xml);
        }

        return path;
    }

    private const string Head =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<savegame><info><save name=\"x\" date=\"1\"/><game id=\"X4\" version=\"900\" build=\"611726\"/></info>";

    [Fact]
    public void ReadsEveryPatchWithItsNameAndVersion()
    {
        string path = Write(
            "a.xml.gz",
            Head + "<patches><patch extension=\"ego_dlc_split\" version=\"900\" name=\"Split Vendetta\"/>" +
            "<patch extension=\"fixture_mod\" version=\"1.2\" name=\"Fixture &amp; Co\"/><history><entry time=\"1\"><patch extension=\"not_a_requirement\"/></entry></history></patches>" +
            "<universe/></savegame>");

        var patches = SavePatchesReader.Read(path);

        Assert.Equal(
            [new SavePatch("ego_dlc_split", "Split Vendetta", "900"), new SavePatch("fixture_mod", "Fixture & Co", "1.2")],
            patches);
    }

    [Fact]
    public void ASaveWithoutPatchesOrAnEmptyBlockYieldsNothing()
    {
        Assert.Empty(SavePatchesReader.Read(Write("none.xml.gz", Head + "<universe/></savegame>")));
        Assert.Empty(SavePatchesReader.Read(Write("empty.xml.gz", Head + "<patches/><universe/></savegame>")));
    }

    [Fact]
    public void OnlyTheFirst64KbAreLookedAt()
    {
        // inside the head: found even though the document is cut off long before it ends
        string early = Head + "<patches><patch extension=\"early\" version=\"1\" name=\"Early\"/></patches><universe>" + new string('x', 400_000);
        Assert.Equal([new SavePatch("early", "Early", "1")], SavePatchesReader.Read(Write("early.xml.gz", early)));

        // a block that starts after the 64 KB prefix is not read
        string late = Head + "<universe>" + new string('x', SavePatchesReader.PrefixBytes + 1000) + "</universe><patches><patch extension=\"late\" version=\"1\" name=\"Late\"/></patches></savegame>";
        Assert.Empty(SavePatchesReader.Read(Write("late.xml.gz", late)));
    }

    [Fact]
    public void AListCutOffByThePrefixKeepsWhatWasRead()
    {
        var text = new StringBuilder(Head).Append("<patches>");
        int i = 0;
        while (text.Length < SavePatchesReader.PrefixBytes + 5000)
        {
            text.Append($"<patch extension=\"ext_{i++}\" version=\"1\" name=\"Extension number {i}\"/>");
        }

        text.Append("</patches></savegame>");
        var patches = SavePatchesReader.Read(Write("long.xml.gz", text.ToString()));
        Assert.NotEmpty(patches);
        Assert.True(patches.Count < i);
        Assert.Equal("ext_0", patches[0].Extension);
    }

    [Fact]
    public void BadFilesYieldAnEmptyListInsteadOfThrowing()
    {
        Assert.Empty(SavePatchesReader.Read(Write("plain.xml.gz", Head + "<patches><patch extension=\"a\"/></patches>", gzip: false)));
        Assert.Empty(SavePatchesReader.Read(Path.Combine(_dir, "missing.xml.gz")));
        File.WriteAllBytes(Path.Combine(_dir, "junk.gz"), [0x1F, 0x8B, 1, 2, 3, 4, 5]);
        Assert.Empty(SavePatchesReader.Read(Path.Combine(_dir, "junk.gz")));
    }

    [Fact]
    public void AnEntryWithoutAnExtensionIdIsSkippedAndMissingAttributesAreEmpty()
    {
        string path = Write("odd.xml.gz", Head + "<patches><patch name=\"nameless\"/><patch extension=\"only_id\"/></patches></savegame>");
        Assert.Equal([new SavePatch("only_id", string.Empty, string.Empty)], SavePatchesReader.Read(path));
    }
}

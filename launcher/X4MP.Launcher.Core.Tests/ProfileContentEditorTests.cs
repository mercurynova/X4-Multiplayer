namespace X4MP.Launcher.Core.Tests;

public class ProfileContentEditorTests
{
    private const string Header = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<content>\n";

    [Fact]
    public void ApplyRewritesEnabledAndRestoreIsByteIdentical()
    {
        const string text = Header + "  <extension id=\"ws_1\" enabled=\"true\"></extension>\n</content>\n";

        var (applied, edits) = ProfileContentEditor.Apply(text, [Item(PlanAction.Disable, "ws_1")]);

        Assert.Contains("id=\"ws_1\" enabled=\"false\"", applied, StringComparison.Ordinal);
        Assert.Single(edits);
        Assert.Equal("<extension id=\"ws_1\" enabled=\"true\"></extension>", edits[0].OriginalTag);

        var (restored, skipped) = ProfileContentEditor.Restore(applied, edits);

        Assert.Equal(text, restored);
        Assert.Empty(skipped);
    }

    [Fact]
    public void ApplyInsertsEnabledWhenTheAttributeIsMissing()
    {
        const string text = Header + "  <extension id=\"abc\" name=\"Abc\">x</extension>\n</content>\n";

        var (applied, edits) = ProfileContentEditor.Apply(text, [Item(PlanAction.Enable, "abc")]);

        Assert.Contains("<extension id=\"abc\" name=\"Abc\" enabled=\"true\">", applied, StringComparison.Ordinal);
        Assert.Equal(text, ProfileContentEditor.Restore(applied, edits).Text);
    }

    [Fact]
    public void ApplyKeepsTheQuoteStyleOfTheExistingValue()
    {
        const string text = Header + "  <extension id='abc' enabled='1'></extension>\n</content>\n";

        var (applied, edits) = ProfileContentEditor.Apply(text, [Item(PlanAction.Disable, "abc")]);

        Assert.Contains("enabled='false'", applied, StringComparison.Ordinal);
        Assert.Equal(text, ProfileContentEditor.Restore(applied, edits).Text);
    }

    [Fact]
    public void ApplyAddsAnEntryForAModTheProfileNeverMentioned()
    {
        const string text = Header + "  <extension id=\"other\" enabled=\"true\"></extension>\n</content>\n";

        var (applied, edits) = ProfileContentEditor.Apply(text, [Item(PlanAction.Enable, "new_mod")]);

        Assert.Contains(
            "  <extension id=\"other\" enabled=\"true\"></extension>\n  <extension id=\"new_mod\" enabled=\"true\"></extension>\n</content>",
            applied,
            StringComparison.Ordinal);
        Assert.Null(edits[0].OriginalTag);
        Assert.Equal(text, ProfileContentEditor.Restore(applied, edits).Text);
    }

    [Fact]
    public void SelfClosingEntryIsRewrittenAndRestored()
    {
        const string text = Header + "  <extension id=\"abc\" enabled=\"1\"/>\n</content>\n";

        var (applied, edits) = ProfileContentEditor.Apply(text, [Item(PlanAction.Disable, "abc")]);

        Assert.Contains("<extension id=\"abc\" enabled=\"false\"/>", applied, StringComparison.Ordinal);
        Assert.Equal(text, ProfileContentEditor.Restore(applied, edits).Text);
    }

    [Fact]
    public void RestoreLeavesAnEntryThePlayerChanged()
    {
        const string text = Header + "  <extension id=\"abc\" enabled=\"true\"></extension>\n</content>\n";
        var (applied, edits) = ProfileContentEditor.Apply(text, [Item(PlanAction.Disable, "abc")]);
        string changedInGame = applied.Replace("enabled=\"false\"", "enabled=\"true\"", StringComparison.Ordinal);

        var (restored, skipped) = ProfileContentEditor.Restore(changedInGame, edits);

        Assert.Equal(changedInGame, restored);
        Assert.Equal(["abc"], skipped);
    }

    [Fact]
    public void RestoreSkipsAnAddedEntryThePlayerRemoved()
    {
        const string text = Header + "</content>\n";
        var (applied, edits) = ProfileContentEditor.Apply(text, [Item(PlanAction.Enable, "new_mod")]);
        string without = ProfileContentEditor.Restore(applied, edits).Text;

        var (restored, skipped) = ProfileContentEditor.Restore(without, edits);

        Assert.Equal(without, restored);
        Assert.Equal(["new_mod"], skipped);
    }

    [Fact]
    public void UnrelatedTagsCommentsAndTextStayAsTheyWere()
    {
        const string text = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<!-- keep me -->\n<content>\n" +
            "  <extension id=\"abc\" enabled=\"true\"></extension>\n" +
            "  <extension id=\"abc_extra\" enabled=\"true\"></extension>\n" +
            "  <dependency id=\"ego_dlc_split\" optional=\"true\"/>\n</content>\n";

        var (applied, edits) = ProfileContentEditor.Apply(text, [Item(PlanAction.Disable, "abc")]);

        Assert.Contains("<!-- keep me -->", applied, StringComparison.Ordinal);
        Assert.Contains("<extension id=\"abc_extra\" enabled=\"true\"></extension>", applied, StringComparison.Ordinal);
        Assert.Contains("<dependency id=\"ego_dlc_split\" optional=\"true\"/>", applied, StringComparison.Ordinal);
        Assert.Equal(text, ProfileContentEditor.Restore(applied, edits).Text);
    }

    [Fact]
    public void PlanLinesThatAreNotTogglesAreIgnored()
    {
        const string text = Header + "</content>\n";

        var (applied, edits) = ProfileContentEditor.Apply(text,
            [Item(PlanAction.Missing, "gone"), Item(PlanAction.VersionMismatch, "old"), Item(PlanAction.Stale, "stale")]);

        Assert.Equal(text, applied);
        Assert.Empty(edits);
    }

    [Fact]
    public void DuplicatePlanLinesForOneIdAreAppliedOnce()
    {
        const string text = Header + "</content>\n";

        var (applied, edits) = ProfileContentEditor.Apply(text,
            [Item(PlanAction.Enable, "abc"), Item(PlanAction.Disable, "abc")]);

        Assert.Single(edits);
        Assert.Contains("id=\"abc\" enabled=\"true\"", applied, StringComparison.Ordinal);
    }

    [Fact]
    public void AnIdWithAQuoteIsEscapedWhenTheEntryIsAdded()
    {
        const string text = Header + "</content>\n";

        var (applied, _) = ProfileContentEditor.Apply(text, [Item(PlanAction.Enable, "a\"b")]);

        Assert.Contains("id=\"a&quot;b\"", applied, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileWithoutAContentElementIsLeftAlone()
    {
        const string text = "<not-content/>\n";

        var (applied, edits) = ProfileContentEditor.Apply(text, [Item(PlanAction.Enable, "abc")]);

        Assert.Equal(text, applied);
        Assert.Empty(edits);
    }

    private static PlanItem Item(PlanAction action, string id) =>
        new(action, id, id, "Sim", "Required", "", null, 0, null, "", false, false);
}

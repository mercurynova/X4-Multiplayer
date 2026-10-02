using X4MP.Core.Mods;
using X4MP.FakeNode;
using X4MP.Proto;

namespace X4MP.Core.Tests.Mods;

/// <summary>M1-X5: the FakeNode expectation model must agree with the real <see cref="ModPolicyEvaluator"/> for every preset pairing and server setting.</summary>
public class FakeNodePresetExpectationTests
{
    public static TheoryData<ModEnforcement, UnknownModDefault> Settings
    {
        get
        {
            var data = new TheoryData<ModEnforcement, UnknownModDefault>();
            foreach (var e in new[] { ModEnforcement.Strict, ModEnforcement.Warn })
                foreach (var u in new[] { UnknownModDefault.AllowClientOnly, UnknownModDefault.AllowAll, UnknownModDefault.Block })
                    data.Add(e, u);
            return data;
        }
    }

    private static string Ids(IEnumerable<ModRefT>? l) => string.Join(',', (l ?? []).Select(r => r.Id).OrderBy(x => x, StringComparer.Ordinal));

    private static string Ids(IEnumerable<ExpectedMod> l) => string.Join(',', l.Select(r => r.Id).OrderBy(x => x, StringComparer.Ordinal));

    [Theory]
    [MemberData(nameof(Settings))]
    public void TheModelAgreesWithTheEvaluator(ModEnforcement enforcement, UnknownModDefault unknown)
    {
        var policy = new ModPolicyT { Version = 1, SourceMode = ModSourceMode.AuthorityDefines, UnknownDefault = unknown, Enforcement = enforcement, Entries = [] };
        var settings = new ModExpectationSettings(enforcement, unknown);
        var authorities = new[] { ExtensionPresets.Vanilla(), ExtensionPresets.Modded() };
        var bots = new List<List<ExtensionInfoT>> { ExtensionPresets.Vanilla(), ExtensionPresets.Modded() };
        bots.AddRange(ExtensionPresets.Variants.Select(ExtensionPresets.Mismatched));

        foreach (var authority in authorities)
        {
            foreach (var bot in bots)
            {
                var real = ModPolicyEvaluator.Evaluate(bot, policy, authority);
                var model = ModExpectation.Compute(bot, authority, settings);
                string where = $"authority={string.Join('+', authority.Select(a => a.Id))} bot={string.Join('+', bot.Select(a => a.Id + (a.Enabled ? "" : "(off)") + "@" + a.Version))}";
                Assert.True(
                    (real.Verdict, model.Outcome) is (ModVerdict.Admit, ExpectedModOutcome.Admit) or (ModVerdict.AdmitWithWarning, ExpectedModOutcome.AdmitWithWarning) or (ModVerdict.Reject, ExpectedModOutcome.Reject),
                    $"{where}: evaluator {real.Verdict}, model {model.Outcome}");
                Assert.Equal(Ids(real.Violation?.Install), Ids(model.Install));
                Assert.Equal(Ids(real.Violation?.Enable), Ids(model.Enable));
                Assert.Equal(Ids(real.Violation?.Disable), Ids(model.Disable));
                Assert.Equal(Ids(real.Violation?.Update), Ids(model.Update));
                Assert.Equal(real.DlcViolation, model.DlcViolation);
                if (real.Violation is not null)
                    Assert.True(model.Matches(real.Violation, out string detail), detail);
            }
        }
    }
}

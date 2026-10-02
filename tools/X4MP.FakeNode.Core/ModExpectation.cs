using System.Globalization;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.FakeNode;

public enum ExpectedModOutcome
{
    /// <summary>Admitted without a notice.</summary>
    Admit,

    /// <summary>Admitted, with a <c>ServerNotice</c> that lists the differences (Warn enforcement, no DLC difference).</summary>
    AdmitWithWarning,

    /// <summary>Closed with <c>Disconnect{ExtensionsMismatch}</c> and a <c>ModPolicyViolation</c>.</summary>
    Reject,
}

/// <summary>One mod in an expected list: id and display name.</summary>
public sealed record ExpectedMod(string Id, string Name);

/// <summary>The server settings the expectation model assumes (<c>X4MP:Mods:Enforcement</c> and <c>X4MP:Mods:UnknownDefault</c>; the source mode is AuthorityDefines).</summary>
public sealed record ModExpectationSettings(ModEnforcement Enforcement = ModEnforcement.Strict, UnknownModDefault UnknownDefault = UnknownModDefault.AllowClientOnly);

/// <summary>What the server should do with one bot: the outcome and the four lists (ordered by id).</summary>
public sealed record ModExpectation(
    ExpectedModOutcome Outcome, IReadOnlyList<ExpectedMod> Install, IReadOnlyList<ExpectedMod> Enable, IReadOnlyList<ExpectedMod> Disable,
    IReadOnlyList<ExpectedMod> Update, bool DlcViolation)
{
    /// <summary><c>admit</c>, <c>warn</c> or <c>reject</c>.</summary>
    public string OutcomeName => Outcome switch { ExpectedModOutcome.Admit => "admit", ExpectedModOutcome.AdmitWithWarning => "warn", _ => "reject" };

    public bool HasViolations => Install.Count + Enable.Count + Disable.Count + Update.Count > 0;

    /// <summary>
    /// An independent model of the server's AuthorityDefines rules (docs/mod-management.md 3.3 to 3.5) for lists without content hashes and
    /// without admin entries: the authority's enabled Dlc and Sim extensions are required (missing = install, off = enable, other version = update);
    /// an enabled extension the authority lacks is disabled when it is Dlc, or Sim/Unknown unless <c>UnknownDefault</c> allows it; the client-only
    /// allowlist and <c>x4native</c>/<c>x4mp</c> never count. Warn admits unless a Dlc difference is involved.
    /// </summary>
    public static ModExpectation Compute(IReadOnlyList<ExtensionInfoT> bot, IReadOnlyList<ExtensionInfoT> authority, ModExpectationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(bot);
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(settings);

        var have = new Dictionary<string, ExtensionInfoT>(StringComparer.Ordinal);
        foreach (var e in bot.Where(e => !string.IsNullOrEmpty(e.Id) && !ExtensionReports.IsHashExcluded(e.Id)))
            have[e.Id] = e;

        var install = new List<ExpectedMod>();
        var enable = new List<ExpectedMod>();
        var disable = new List<ExpectedMod>();
        var update = new List<ExpectedMod>();
        bool dlc = false;
        var required = new HashSet<string>(StringComparer.Ordinal);

        foreach (var a in authority.Where(a => a.Enabled && !string.IsNullOrEmpty(a.Id) && !ExtensionReports.IsHashExcluded(a.Id)).OrderBy(a => a.Id, StringComparer.Ordinal))
        {
            var cls = ExtensionReports.Classify(a.Id, a);
            if (cls is not (ExtensionClass.Dlc or ExtensionClass.Sim))
                continue;
            required.Add(a.Id);
            var mod = new ExpectedMod(a.Id, string.IsNullOrEmpty(a.Name) ? a.Id : a.Name);
            if (!have.TryGetValue(a.Id, out var p))
            {
                install.Add(mod);
                dlc |= cls == ExtensionClass.Dlc;
            }
            else if (!p.Enabled)
            {
                enable.Add(mod);
                dlc |= cls == ExtensionClass.Dlc;
            }
            else if (!string.Equals((p.Version ?? string.Empty).Trim(), (a.Version ?? string.Empty).Trim(), StringComparison.Ordinal))
            {
                update.Add(mod);
                dlc |= cls == ExtensionClass.Dlc;
            }
        }

        foreach (var p in have.Values.Where(p => p.Enabled && !required.Contains(p.Id)).OrderBy(p => p.Id, StringComparer.Ordinal))
        {
            var cls = ExtensionReports.Classify(p.Id, p);
            bool reject;
            if (cls == ExtensionClass.Dlc)
            {
                reject = true;
                dlc = true;
            }
            else if (ExtensionReports.IsAllowlisted(p.Id))
            {
                reject = false;
            }
            else
            {
                reject = settings.UnknownDefault switch
                {
                    UnknownModDefault.AllowAll => false,
                    UnknownModDefault.Block => true,
                    _ => cls != ExtensionClass.ClientOnly,
                };
            }

            if (reject)
                disable.Add(new ExpectedMod(p.Id, string.IsNullOrEmpty(p.Name) ? p.Id : p.Name));
        }

        bool any = install.Count + enable.Count + disable.Count + update.Count > 0;
        var outcome = !any ? ExpectedModOutcome.Admit : settings.Enforcement == ModEnforcement.Warn && !dlc ? ExpectedModOutcome.AdmitWithWarning : ExpectedModOutcome.Reject;
        return new ModExpectation(outcome, install, enable, disable, update, dlc);
    }

    /// <summary><c>install=[a,b] enable=[] disable=[c] update=[]</c> (ids).</summary>
    public string Describe() => string.Create(CultureInfo.InvariantCulture,
        $"install=[{Ids(Install)}] enable=[{Ids(Enable)}] disable=[{Ids(Disable)}] update=[{Ids(Update)}]");

    private static string Ids(IEnumerable<ExpectedMod> mods) => string.Join(',', mods.Select(m => m.Id));

    /// <summary>Describes the lists a rejection carried, in the same format as <see cref="Describe()"/>.</summary>
    public static string Describe(ModPolicyViolationT v)
    {
        ArgumentNullException.ThrowIfNull(v);
        static string One(List<ModRefT>? list) => string.Join(',', (list ?? []).Select(r => r.Id));
        return $"install=[{One(v.Install)}] enable=[{One(v.Enable)}] disable=[{One(v.Disable)}] update=[{One(v.Update)}]";
    }

    /// <summary>The Workshop links the violation carries, <c>id=url</c> (only mods that have one).</summary>
    public static string Links(ModPolicyViolationT v)
    {
        ArgumentNullException.ThrowIfNull(v);
        var all = (v.Install ?? []).Concat(v.Enable ?? []).Concat(v.Disable ?? []).Concat(v.Update ?? []);
        return string.Join(' ', all.Where(r => r.WorkshopId != 0).Select(r => $"{r.Id}={ModLinks.WorkshopUrl(r.WorkshopId)}"));
    }

    /// <summary>True when the violation carries exactly the expected ids in each list (the Workshop id of a <c>ws_</c> mod must be its own).</summary>
    public bool Matches(ModPolicyViolationT v, out string detail)
    {
        ArgumentNullException.ThrowIfNull(v);
        var problems = new List<string>();
        void Check(string label, IReadOnlyList<ExpectedMod> expected, List<ModRefT>? actual)
        {
            var want = expected.Select(m => m.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var got = (actual ?? []).Select(r => r.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();
            if (!want.SequenceEqual(got, StringComparer.Ordinal))
                problems.Add($"{label}: expected [{string.Join(',', want)}] got [{string.Join(',', got)}]");
            foreach (var r in actual ?? [])
            {
                ulong ws = ModLinks.WorkshopIdOf(r.Id);
                if (ws != 0 && r.WorkshopId != ws)
                    problems.Add($"{label}: {r.Id} has workshop id {r.WorkshopId}, expected {ws}");
            }
        }

        Check("install", Install, v.Install);
        Check("enable", Enable, v.Enable);
        Check("disable", Disable, v.Disable);
        Check("update", Update, v.Update);
        detail = string.Join("; ", problems);
        return problems.Count == 0;
    }
}

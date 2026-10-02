using System.Globalization;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.Core.Mods;

public enum ModVerdict
{
    /// <summary>No violations.</summary>
    Admit,

    /// <summary>Violations, but enforcement is Warn and none is a Dlc violation: admit and flag the player.</summary>
    AdmitWithWarning,

    /// <summary>Reject with <see cref="ModEvaluation.Violation"/>.</summary>
    Reject,
}

/// <param name="Verdict">What to do.</param>
/// <param name="Violation">The exact lists (null when there are none).</param>
/// <param name="DlcViolation">True when a Dlc mismatch is part of the violations (always rejects).</param>
public sealed record ModEvaluation(ModVerdict Verdict, ModPolicyViolationT? Violation, bool DlcViolation)
{
    public static ModEvaluation Admitted { get; } = new(ModVerdict.Admit, null, false);
}

/// <summary>
/// Pure evaluation of one node's extension report against the session mod policy (docs/mod-management.md 3.3 to 3.5).
/// <list type="bullet">
/// <item>Classification: client-only allowlist, then Dlc by id/source/hint, then the admin override, then the node's hint; Unknown = Sim.</item>
/// <item><c>x4native</c> and <c>x4mp</c> are ignored (compared through the version fields).</item>
/// <item>Required: not installed = install, installed but disabled = enable, wrong version or content hash = update.
/// A Required entry the admin switched off (<c>enabled = false</c>) acts as Blocked.</item>
/// <item>Blocked: enabled on the player = disable. Allowed: never rejects for presence; a Sim/Dlc copy must still satisfy the entry's version rule.</item>
/// <item>Enabled extensions not in the list: Dlc is a violation (ADR-004), the rest follows <c>unknown_default</c>.</item>
/// <item>AuthorityDefines: the authority's enabled Dlc and Sim extensions are implicit Required entries (explicit entries win); AdminList
/// still derives implicit Required entries for the authority's Dlc. With no authority yet AuthorityDefines evaluates nothing.</item>
/// <item>Warn admits and flags, except that Dlc violations always reject. Allowlisted libraries never produce version or hash violations.</item>
/// </list>
/// </summary>
public static class ModPolicyEvaluator
{
    private sealed record Rule(
        string Id, string Name, ModRule Effective, ExtensionClass Class, VersionRule VersionRule, string Version, byte[] Hash,
        string NexusUrl, ulong WorkshopId, string Notes);

    public static ModEvaluation Evaluate(
        IReadOnlyList<ExtensionInfoT> player, ModPolicyT policy, IReadOnlyList<ExtensionInfoT>? authority)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(policy);

        if (policy.SourceMode == ModSourceMode.AuthorityDefines && authority is null)
        {
            return ModEvaluation.Admitted;
        }

        var rules = BuildRules(policy, authority);
        var have = new Dictionary<string, ExtensionInfoT>(StringComparer.Ordinal);
        foreach (var p in player)
        {
            if (!string.IsNullOrEmpty(p.Id) && !ExtensionReports.IsHashExcluded(p.Id))
            {
                have[p.Id] = p; // a duplicate id: the last one wins
            }
        }

        var install = new List<ModRefT>();
        var enable = new List<ModRefT>();
        var disable = new List<ModRefT>();
        var update = new List<ModRefT>();
        bool dlc = false;

        foreach (var rule in rules)
        {
            have.TryGetValue(rule.Id, out var p);
            var cls = ExtensionReports.Classify(rule.Id, p, rule.Class);
            bool isDlc = cls == ExtensionClass.Dlc;
            bool library = ExtensionReports.IsAllowlisted(rule.Id);
            switch (rule.Effective)
            {
                case ModRule.Required:
                    if (p is null)
                    {
                        install.Add(Ref(rule, null));
                        dlc |= isDlc;
                    }
                    else if (!p.Enabled)
                    {
                        enable.Add(Ref(rule, p));
                        dlc |= isDlc;
                    }
                    else if (!library && !Satisfies(rule, p, cls))
                    {
                        update.Add(Ref(rule, p));
                        dlc |= isDlc;
                    }

                    break;
                case ModRule.Blocked:
                    if (p is { Enabled: true })
                    {
                        disable.Add(Ref(rule, p));
                        dlc |= isDlc;
                    }

                    break;
                default: // Allowed
                    if (p is { Enabled: true } && !library && cls != ExtensionClass.ClientOnly && !Satisfies(rule, p, cls))
                    {
                        update.Add(Ref(rule, p));
                        dlc |= isDlc;
                    }

                    break;
            }
        }

        var listed = rules.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var p in have.Values.Where(p => p.Enabled && !listed.Contains(p.Id)).OrderBy(p => p.Id, StringComparer.Ordinal))
        {
            var cls = ExtensionReports.Classify(p.Id, p);
            bool reject;
            if (cls == ExtensionClass.Dlc)
            {
                reject = authority is not null; // an extra Dlc: it must match the authority's (ADR-004)
                dlc |= reject;
            }
            else if (ExtensionReports.IsAllowlisted(p.Id))
            {
                reject = false;
            }
            else
            {
                reject = policy.UnknownDefault switch
                {
                    UnknownModDefault.AllowAll => false,
                    UnknownModDefault.Block => true,
                    _ => cls != ExtensionClass.ClientOnly,
                };
            }

            if (reject)
            {
                disable.Add(new ModRefT
                {
                    Id = p.Id,
                    Name = string.IsNullOrEmpty(p.Name) ? p.Id : p.Name,
                    Version = string.Empty,
                    HaveVersion = p.Version ?? string.Empty,
                    NexusUrl = string.Empty,
                    WorkshopId = p.WorkshopId != 0 ? p.WorkshopId : ModLinks.WorkshopIdOf(p.Id),
                    Notes = "not part of this session",
                });
            }
        }

        if (install.Count + enable.Count + disable.Count + update.Count == 0)
        {
            return ModEvaluation.Admitted;
        }

        var violation = new ModPolicyViolationT { PolicyVersion = policy.Version, Install = install, Enable = enable, Disable = disable, Update = update };
        var verdict = policy.Enforcement == ModEnforcement.Warn && !dlc ? ModVerdict.AdmitWithWarning : ModVerdict.Reject;
        return new ModEvaluation(verdict, violation, dlc);
    }

    /// <summary>One line per list, for logs and the Warn-mode notice, e.g. <c>install: A, B; disable: C</c>.</summary>
    public static string Describe(ModPolicyViolationT v)
    {
        var parts = new List<string>();
        void Add(string label, List<ModRefT>? list)
        {
            if (list is { Count: > 0 })
            {
                parts.Add($"{label}: {string.Join(", ", list.Select(r => string.IsNullOrEmpty(r.Name) ? r.Id : r.Name))}");
            }
        }

        Add("install", v.Install);
        Add("enable", v.Enable);
        Add("disable", v.Disable);
        Add("update", v.Update);
        return string.Join("; ", parts);
    }

    private static List<Rule> BuildRules(ModPolicyT policy, IReadOnlyList<ExtensionInfoT>? authority)
    {
        var rules = new List<Rule>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in policy.Entries ?? [])
        {
            if (string.IsNullOrEmpty(e.Id) || !seen.Add(e.Id) || ExtensionReports.IsHashExcluded(e.Id))
            {
                continue;
            }

            var effective = e.Rule == ModRule.Required && !e.Enabled ? ModRule.Blocked : e.Rule;
            rules.Add(new Rule(
                e.Id, e.Name ?? string.Empty, effective, e.ModClass, e.VersionRule, e.Version ?? string.Empty, e.ContentHash?.ToArray() ?? [],
                e.NexusUrl ?? string.Empty, e.WorkshopId != 0 ? e.WorkshopId : ModLinks.WorkshopIdOf(e.Id), e.Notes ?? string.Empty));
        }

        if (authority is not null)
        {
            bool simToo = policy.SourceMode == ModSourceMode.AuthorityDefines;
            foreach (var a in authority.Where(a => a.Enabled && !string.IsNullOrEmpty(a.Id) && !ExtensionReports.IsHashExcluded(a.Id)).OrderBy(a => a.Id, StringComparer.Ordinal))
            {
                var cls = ExtensionReports.Classify(a.Id, a);
                if ((cls == ExtensionClass.Dlc || (simToo && cls == ExtensionClass.Sim)) && seen.Add(a.Id))
                {
                    rules.Add(new Rule(
                        a.Id, a.Name ?? string.Empty, ModRule.Required, cls, VersionRule.Exact, a.Version ?? string.Empty, a.ContentHash?.ToArray() ?? [],
                        string.Empty, a.WorkshopId != 0 ? a.WorkshopId : ModLinks.WorkshopIdOf(a.Id), string.Empty));
                }
            }
        }

        return rules;
    }

    private static bool Satisfies(Rule rule, ExtensionInfoT p, ExtensionClass cls)
    {
        if (rule.VersionRule == VersionRule.Any)
        {
            return true;
        }

        if (!VersionOk(rule.VersionRule, rule.Version, p.Version ?? string.Empty))
        {
            return false;
        }

        // A content hash only counts for sim-affecting mods when both sides have one (mod-management 3.2).
        if (cls != ExtensionClass.ClientOnly && rule.Hash.Length > 0 && p.ContentHash is { Count: > 0 } theirs && !theirs.SequenceEqual(rule.Hash))
        {
            return false;
        }

        return true;
    }

    private static bool VersionOk(VersionRule rule, string wanted, string have)
    {
        if (rule == VersionRule.Any || string.IsNullOrWhiteSpace(wanted))
        {
            return true;
        }

        wanted = wanted.Trim();
        have = have.Trim();
        return rule == VersionRule.Exact ? string.Equals(wanted, have, StringComparison.Ordinal) : CompareVersions(have, wanted) >= 0;
    }

    /// <summary>Dotted/dashed segments compared numerically when both are numbers, else ordinally; missing segments count as 0.</summary>
    public static int CompareVersions(string a, string b)
    {
        var sa = a.Split('.', '-', '_');
        var sb = b.Split('.', '-', '_');
        for (int i = 0; i < Math.Max(sa.Length, sb.Length); i++)
        {
            string x = i < sa.Length ? sa[i] : "0";
            string y = i < sb.Length ? sb[i] : "0";
            int c = ulong.TryParse(x, NumberStyles.None, CultureInfo.InvariantCulture, out ulong nx) && ulong.TryParse(y, NumberStyles.None, CultureInfo.InvariantCulture, out ulong ny)
                ? nx.CompareTo(ny)
                : string.CompareOrdinal(x, y);
            if (c != 0)
            {
                return c;
            }
        }

        return 0;
    }

    private static ModRefT Ref(Rule r, ExtensionInfoT? p) => new()
    {
        Id = r.Id,
        Name = !string.IsNullOrEmpty(r.Name) ? r.Name : !string.IsNullOrEmpty(p?.Name) ? p!.Name : r.Id,
        Version = r.Version,
        HaveVersion = p?.Version ?? string.Empty,
        NexusUrl = r.NexusUrl,
        WorkshopId = r.WorkshopId,
        Notes = r.Notes,
    };
}

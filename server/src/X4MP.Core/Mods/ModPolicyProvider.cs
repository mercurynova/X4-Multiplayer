using X4MP.Core.Settings;
using X4MP.Proto;

namespace X4MP.Core.Mods;

/// <summary>
/// Where the gateway (and later the Welcome builder and the admin API) reads the session mod policy. M1-X3 adds a persistent
/// implementation behind the same interface; M1-X4 edits go through it and push <c>ModPolicyChanged</c>.
/// </summary>
public interface IModPolicyProvider
{
    /// <summary>The current policy. Treat as read-only: it is shared; take <see cref="ModPolicyCopy.Clone"/> to edit.</summary>
    ModPolicyT Current { get; }
}

/// <summary>
/// In-memory policy: the three knobs (source mode, unknown default, enforcement) come from the live <see cref="ModManagementOptions"/>
/// settings, the entries from <see cref="SetEntries"/>. <c>version</c> bumps whenever either changes.
/// </summary>
public sealed class InMemoryModPolicyProvider : IModPolicyProvider
{
    private readonly Func<ModManagementOptions> _settings;
    private readonly object _gate = new();
    private List<ModPolicyEntryT> _entries = [];
    private uint _version = 1;
    private ModPolicyT? _cached;
    private (ModSourceMode Mode, UnknownModDefault Unknown, ModEnforcement Enforcement) _knobs;

    public InMemoryModPolicyProvider(Func<ModManagementOptions>? settings = null)
    {
        _settings = settings ?? (static () => new ModManagementOptions());
    }

    /// <summary>Raised after <see cref="SetEntries"/> with the new policy (not for settings-driven changes).</summary>
    public event Action<ModPolicyT>? Changed;

    public ModPolicyT Current
    {
        get
        {
            lock (_gate)
            {
                var s = _settings();
                var knobs = (s.SourceMode, s.UnknownDefault, s.Enforcement);
                if (_cached is null || knobs != _knobs)
                {
                    if (_cached is not null)
                    {
                        _version++;
                    }

                    _knobs = knobs;
                    _cached = Build();
                }

                return _cached;
            }
        }
    }

    /// <summary>Replaces the entry list and bumps the version.</summary>
    public ModPolicyT SetEntries(IEnumerable<ModPolicyEntryT> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ModPolicyT policy;
        lock (_gate)
        {
            _entries = [.. entries];
            _version++;
            var s = _settings();
            _knobs = (s.SourceMode, s.UnknownDefault, s.Enforcement);
            policy = _cached = Build();
        }

        Changed?.Invoke(policy);
        return policy;
    }

    private ModPolicyT Build() => new()
    {
        Version = _version,
        SourceMode = _knobs.Mode,
        UnknownDefault = _knobs.Unknown,
        Enforcement = _knobs.Enforcement,
        Entries = [.. _entries],
    };
}

public static class ModPolicyCopy
{
    /// <summary>True when two policies have the same knobs and the same entries in the same order (the version is not compared).</summary>
    public static bool Equivalent(ModPolicyT a, ModPolicyT b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.SourceMode != b.SourceMode || a.UnknownDefault != b.UnknownDefault || a.Enforcement != b.Enforcement)
        {
            return false;
        }

        var x = a.Entries ?? [];
        var y = b.Entries ?? [];
        if (x.Count != y.Count)
        {
            return false;
        }

        for (int i = 0; i < x.Count; i++)
        {
            var l = x[i];
            var r = y[i];
            if (l.Id != r.Id || l.Name != r.Name || l.Rule != r.Rule || l.Enabled != r.Enabled || l.ModClass != r.ModClass
                || l.VersionRule != r.VersionRule || l.Version != r.Version || l.NexusUrl != r.NexusUrl || l.WorkshopId != r.WorkshopId
                || l.Notes != r.Notes || !(l.ContentHash ?? []).SequenceEqual(r.ContentHash ?? []))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A deep-enough copy (entries are copied) to edit without touching the shared instance.</summary>
    public static ModPolicyT Clone(this ModPolicyT policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return new ModPolicyT
        {
            Version = policy.Version,
            SourceMode = policy.SourceMode,
            UnknownDefault = policy.UnknownDefault,
            Enforcement = policy.Enforcement,
            Entries = [.. (policy.Entries ?? []).Select(e => new ModPolicyEntryT
            {
                Id = e.Id, Name = e.Name, Rule = e.Rule, Enabled = e.Enabled, ModClass = e.ModClass, VersionRule = e.VersionRule, Version = e.Version,
                ContentHash = e.ContentHash is null ? null : [.. e.ContentHash], NexusUrl = e.NexusUrl, WorkshopId = e.WorkshopId, Notes = e.Notes,
            })],
        };
    }
}

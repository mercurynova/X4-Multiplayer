using X4MP.Core.Settings;
using X4MP.Proto;

namespace X4MP.Core.Mods;

/// <summary>A policy provider whose policy can be edited (task M1-X3/X4): every real change bumps <c>version</c>, is stored and raises <see cref="Changed"/>.</summary>
public interface IModPolicyEditor : IModPolicyProvider
{
    /// <summary>Raised after every change (admin edit or a change of the <c>X4MP:Mods</c> settings) with the new policy. Never raised while a lock is held.</summary>
    event Action<ModPolicyT>? Changed;

    /// <summary>
    /// Applies <paramref name="edit"/> to a copy of the policy. When the copy differs the version goes up by one, the result is stored and
    /// <see cref="Changed"/> is raised; an edit that changes nothing returns the current policy untouched.
    /// </summary>
    ModPolicyT Update(string updatedBy, Action<ModPolicyT> edit);
}

/// <summary>
/// The session mod policy kept in the database (<see cref="IModStore"/>), loaded on first use. Until the first edit the three knobs (source mode,
/// unknown default, enforcement) come from the live <c>X4MP:Mods</c> settings and the list is empty. After that the stored policy is the truth, and
/// a later change of a setting is adopted as a newer edit (the version goes up), so the Settings page and the Mods page never disagree for long:
/// the last writer wins.
/// </summary>
public sealed class PersistentModPolicyProvider : IModPolicyEditor
{
    private readonly IModStore _store;
    private readonly Func<ModManagementOptions> _settings;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private bool _loaded;
    private ModPolicyT _current = new() { Version = 1, Entries = [] };
    private (ModSourceMode Mode, UnknownModDefault Unknown, ModEnforcement Enforcement) _lastSettings;

    public PersistentModPolicyProvider(IModStore store, Func<ModManagementOptions>? settings = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _settings = settings ?? (static () => new ModManagementOptions());
        _time = time ?? TimeProvider.System;
    }

    public event Action<ModPolicyT>? Changed;

    public ModPolicyT Current
    {
        get
        {
            ModPolicyT result;
            ModPolicyT? adopted = null;
            lock (_gate)
            {
                EnsureLoaded();
                var s = _settings();
                var knobs = (s.SourceMode, s.UnknownDefault, s.Enforcement);
                if (knobs != _lastSettings)
                {
                    _lastSettings = knobs;
                    if ((_current.SourceMode, _current.UnknownDefault, _current.Enforcement) != knobs)
                    {
                        var next = _current.Clone();
                        next.SourceMode = knobs.SourceMode;
                        next.UnknownDefault = knobs.UnknownDefault;
                        next.Enforcement = knobs.Enforcement;
                        next.Version = _current.Version + 1;
                        Persist(next, "settings");
                        _current = next;
                        adopted = next;
                    }
                }

                result = _current;
            }

            if (adopted is not null)
            {
                Changed?.Invoke(adopted);
            }

            return result;
        }
    }

    public ModPolicyT Update(string updatedBy, Action<ModPolicyT> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        ModPolicyT changed;
        lock (_gate)
        {
            EnsureLoaded();
            var next = _current.Clone();
            edit(next);
            if (ModPolicyCopy.Equivalent(_current, next))
            {
                return _current;
            }

            next.Version = _current.Version + 1;
            Persist(next, updatedBy);
            _current = changed = next;
        }

        Changed?.Invoke(changed);
        return changed;
    }

    private void Persist(ModPolicyT policy, string by) => _store.SavePolicy(new StoredModPolicy(
        policy.Version, policy.SourceMode, policy.UnknownDefault, policy.Enforcement, _time.GetUtcNow(), by, [.. policy.Entries ?? []]));

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        var s = _settings();
        _lastSettings = (s.SourceMode, s.UnknownDefault, s.Enforcement);
        if (_store.LoadPolicy() is { } stored)
        {
            _current = new ModPolicyT
            {
                Version = stored.Version,
                SourceMode = stored.SourceMode,
                UnknownDefault = stored.UnknownDefault,
                Enforcement = stored.Enforcement,
                Entries = [.. stored.Entries],
            };
        }
        else
        {
            _current = new ModPolicyT { Version = 1, SourceMode = s.SourceMode, UnknownDefault = s.UnknownDefault, Enforcement = s.Enforcement, Entries = [] };
        }
    }
}

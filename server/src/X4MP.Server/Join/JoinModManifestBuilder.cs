using Microsoft.Extensions.Options;
using X4MP.Core.Mods;
using X4MP.Core.Net;
using X4MP.Core.Session;
using X4MP.Proto;
using X4MP.Protocol;
using X4MP.Server.Api;

namespace X4MP.Server.Join;

/// <summary>
/// Builds the join manifest from the same effective list the session actor enforces (mod-management 4.1), so the launcher and the
/// admission check can never disagree about what the session expects.
/// </summary>
public sealed class JoinModManifestBuilder(
    IModPolicyProvider policy, GatewayState gateway, SessionActor actor, IOptions<NetOptions> net)
{
    public JoinModManifestDto Build()
    {
        var current = policy.Current;
        var authority = gateway.Authority?.ExtensionList;
        var known = new Dictionary<string, ExtensionInfoT>(StringComparer.Ordinal);
        foreach (var a in authority ?? [])
        {
            if (!string.IsNullOrEmpty(a.Id))
            {
                known[a.Id] = a;
            }
        }

        var mods = ModPolicyEvaluator.EffectiveEntries(current, authority)
            .Select(r =>
            {
                // The admin class wins; without one the authority's own copy decides, and an unknown extension is a Sim extension.
                var cls = ExtensionReports.Classify(r.Id, known.TryGetValue(r.Id, out var info) ? info : null, r.Class);
                return new JoinModDto(
                    r.Id,
                    string.IsNullOrEmpty(r.Name) ? r.Id : r.Name,
                    r.Rule.ToString(),
                    r.Enabled,
                    cls.ToString(),
                    r.Version,
                    (long)r.WorkshopId,
                    ModLinks.TryNormaliseNexusUrl(r.NexusUrl, out var nexus) ? nexus : null,
                    r.Notes);
            })
            .ToList();

        string session = actor.Snapshot.SessionName;
        string build = gateway.Authority?.GameBuild ?? string.Empty;
        if (build.Length == 0)
        {
            build = net.Value.SupportedGameBuilds.FirstOrDefault() ?? string.Empty;
        }

        return new JoinModManifestDto(
            session.Length > 0 ? session : gateway.ServerName,
            current.Version,
            build,
            current.UnknownDefault.ToString(),
            mods);
    }
}

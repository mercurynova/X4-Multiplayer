using X4MP.Core.Mods;
using X4MP.Core.Net;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.Core.Session;

public sealed partial class SessionActor
{
    /// <summary>The session mod policy (set by the host). Without it nothing is re-checked.</summary>
    public IModPolicyProvider? ModPolicy { get; set; }

    /// <summary>Where a re-check that finds a violation stores its report (set by the host).</summary>
    public IModStore? ModStore { get; set; }

    /// <summary>
    /// A node that joined before any authority had reported its extensions was admitted without a real mod check (policy source
    /// <c>AuthorityDefines</c>; docs/mod-management.md 3.4a). Now the authority is known, so judge each such node by the same rules the gateway uses:
    /// Strict mismatch = <c>Disconnect{ExtensionsMismatch}</c> with the <c>ModPolicyViolation</c> and the slot is freed; Warn = the usual notice. Each
    /// violation is stored as a new report. Runs on the actor thread.
    /// </summary>
    private void RecheckPendingMods(AuthorityIdentity authority)
    {
        if (ModPolicy is not { } provider)
        {
            return;
        }

        foreach (var slot in _nodes.Values.ToList())
        {
            if (slot.IsAuthority || slot.Attached is not { ModCheckPending: true } node)
            {
                continue;
            }

            node.ModCheckPending = false;
            var policy = provider.Current;
            var items = ExtensionReports.FromHello(node.Hello);
            if (items.Count > ModPolicyConstants.MaxExtensionEntries)
            {
                items = [.. items.Take(ModPolicyConstants.MaxExtensionEntries)];
            }

            var evaluation = ModPolicyEvaluator.Evaluate(items, policy, authority.ExtensionList);
            if (evaluation.Verdict == ModVerdict.Admit)
            {
                continue;
            }

            var violation = evaluation.Violation!;
            bool reject = evaluation.Verdict == ModVerdict.Reject;
            ModStore?.RecordReport(new ExtensionReportRecord(
                slot.PlayerId, SessionId, _time.GetUtcNow(), node.Hello.ExtensionsHash?.ToArray() ?? [], items,
                reject ? ModReportOutcome.Rejected : ModReportOutcome.Warned, violation, policy.Version));
            string diff = ModPolicyEvaluator.Describe(violation);
            LogModRecheck(slot.PlayerId, slot.Name, reject, diff);
            if (reject)
            {
                LeaveNode(
                    slot, "your mods do not match this session: " + diff, closeWith: DisconnectCode.ExtensionsMismatch, modViolation: violation);
            }
            else if (slot.Announced)
            {
                var notice = new ServerNoticeT
                {
                    Severity = NoticeSeverity.Warning,
                    Text = $"Your mods differ from this session's list ({diff}). You were admitted because mod enforcement is set to Warn.",
                    DisplayMs = 15000,
                };
                SendTo(slot, ControlFrames.Encode(MsgType.ServerNotice, fbb => ServerNotice.Pack(fbb, notice).Value, 256));
            }
        }
    }

    [Microsoft.Extensions.Logging.LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Information, Message = "player {PlayerId} ({Name}): mods checked once the authority reported (rejected: {Rejected}): {Diff}")]
    private partial void LogModRecheck(int playerId, string name, bool rejected, string diff);
}

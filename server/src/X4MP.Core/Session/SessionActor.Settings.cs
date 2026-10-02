using System.Text.Json;
using X4MP.Core.Net;
using X4MP.Core.Settings;
using X4MP.Proto;
using X4MP.Protocol;

namespace X4MP.Core.Session;

public sealed partial class SessionActor
{
    /// <summary>Sends the current node-relevant settings to one node (right after Welcome, SessionState and the roster). Nothing before the first snapshot.</summary>
    private void SendSettings(SessionNode slot)
    {
        if (_settings is { } snapshot)
        {
            SendTo(slot, EncodeSettings(snapshot));
        }
    }

    /// <summary>Sends the full node-relevant set to every announced node (a change of any flagged live setting).</summary>
    private void BroadcastSettings(SessionSettingsSnapshot snapshot) => Broadcast(EncodeSettings(snapshot));

    /// <summary>
    /// Tells every announced node the session mod policy changed (<c>ModPolicyChanged</c>, task M1-X4). Nobody is kicked: a node that no longer
    /// matches is judged at its next join. A node that attaches later reads the policy from its Welcome.
    /// </summary>
    public bool PushModPolicy(X4MP.Proto.ModPolicyT policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return Post(() => Broadcast(ControlFrames.Encode(
            MsgType.ModPolicyChanged, fbb => ModPolicyChanged.Pack(fbb, new ModPolicyChangedT { Policy = policy }).Value, 1024)));
    }

    private static OutboundFrame EncodeSettings(SessionSettingsSnapshot snapshot)
    {
        var update = new ServerSettingsUpdateT { Version = (ulong)Math.Max(0, snapshot.Version), Entries = [] };
        foreach (var (key, value) in snapshot.Values.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            update.Entries.Add(new SettingEntryT { Key = key, Value = WireValue(value) });
        }

        return ControlFrames.Encode(MsgType.ServerSettingsUpdate, fbb => ServerSettingsUpdate.Pack(fbb, update).Value, 256);
    }

    /// <summary>Text for strings (enum names too), otherwise the raw JSON (<c>true</c>, <c>30</c>, <c>["a"]</c>).</summary>
    internal static string WireValue(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText();
}

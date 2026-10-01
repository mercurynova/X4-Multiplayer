using System.Text.Json;

namespace X4MP.Core.Settings;

/// <summary>
/// The settings marked <see cref="SettingAttribute.PushToNodes"/> at one moment (the <c>SessionSettings</c> message
/// of server-design 2.9). <see cref="Version"/> increases with every change.
/// </summary>
public sealed record SessionSettingsSnapshot(long Version, IReadOnlyDictionary<string, JsonElement> Values);

/// <summary>
/// Hook for pushing session settings to connected nodes. The protocol message does not exist yet, so the default
/// implementation does nothing. The session layer registers its own implementation later, and also asks the
/// settings service for a snapshot when a node is admitted.
/// </summary>
public interface ISessionSettingsPusher
{
    ValueTask PushAsync(SessionSettingsSnapshot snapshot, CancellationToken cancellationToken);
}

/// <summary>Default pusher: nobody to push to yet.</summary>
public sealed class NullSessionSettingsPusher : ISessionSettingsPusher
{
    public ValueTask PushAsync(SessionSettingsSnapshot snapshot, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

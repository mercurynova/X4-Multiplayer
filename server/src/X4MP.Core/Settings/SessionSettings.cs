using System.Text.Json;

namespace X4MP.Core.Settings;

/// <summary>
/// The settings marked <see cref="SettingAttribute.PushToNodes"/> at one moment (sent to nodes as <c>ServerSettingsUpdate</c>,
/// server-design 2.9). <see cref="Version"/> increases with every change.
/// </summary>
public sealed record SessionSettingsSnapshot(long Version, IReadOnlyDictionary<string, JsonElement> Values);

/// <summary>
/// Hook for pushing session settings to connected nodes. The session actor implements it (sends <c>ServerSettingsUpdate</c>);
/// the default does nothing (a host without a session layer).
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

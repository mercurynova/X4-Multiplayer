namespace X4MP.Launcher.Core;

/// <summary>Where an extension folder was found; the order is the native one, and a lower rank wins a duplicate id.</summary>
public enum ModSource
{
    Dlc,
    Install,
    User,
    Workshop,
}

/// <summary>The classification the server and the game agree on (docs/mod-management.md 2). Unknown is treated as Sim.</summary>
public enum ModClassHint
{
    Unknown,
    Dlc,
    Sim,
    ClientOnly,
}

/// <summary>One extension folder found on this machine, as far as the launcher needs to know it.</summary>
public sealed record LocalExtension(
    string Folder,
    ModSource Source,
    string Id,
    string Name,
    string Version,
    bool EnabledByDefault,
    bool SaveDependent,
    ulong WorkshopId,
    IReadOnlyList<ContentDependency> Dependencies,
    ModClassHint Class,
    bool HasNativeDll,
    bool ReplacesBaseGame,
    string? Error,
    string? Warning)
{
    /// <summary>The name to show: the content.xml name, then the id, then the folder name.</summary>
    public string DisplayName =>
        !string.IsNullOrEmpty(Name) ? Name
        : !string.IsNullOrEmpty(Id) ? Id
        : Path.GetFileName(Folder);
}

namespace X4MP.Server.Hosting;

/// <summary>
/// Data-dir resolution (server-design 1.5): <c>--data-dir</c> argument, then <c>X4MP_DATA_DIR</c>
/// (an env var is fine for the server; the ban applies to the game mod only), then
/// <c>%ProgramData%\X4MP</c> when running as a service, else <c>./data</c> next to the exe.
/// </summary>
public static class DataDirResolver
{
    public const string EnvironmentVariable = "X4MP_DATA_DIR";

    public static string Resolve(string? argumentValue, string? environmentValue, bool isService, string exeDirectory)
    {
        if (!string.IsNullOrWhiteSpace(argumentValue))
        {
            return Path.GetFullPath(argumentValue);
        }
        if (!string.IsNullOrWhiteSpace(environmentValue))
        {
            return Path.GetFullPath(environmentValue);
        }
        return isService ? ServiceDataDir() : Path.GetFullPath(Path.Combine(exeDirectory, "data"));
    }

    /// <summary>Default data dir for the Windows service: <c>%ProgramData%\X4MP</c>.</summary>
    public static string ServiceDataDir()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return Path.Combine(programData, "X4MP");
    }
}

using System.IO;
using System.Security;
using Microsoft.Win32;

namespace X4MP.Launcher;

/// <summary>
/// The Steam folders the game can live in. Kept out of X4MP.Launcher.Core so the scanner stays testable: the tests
/// pass the folders in, this class is the only part that reads the registry.
/// </summary>
internal static class SteamLocator
{
    public static IReadOnlyList<string> Roots()
    {
        var roots = new List<string>();
        Add(roots, Read(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath"));
        Add(roots, Read(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"));
        Add(roots, Read(@"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam", "InstallPath"));
        Add(roots, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
        return roots;
    }

    private static string? Read(string key, string name)
    {
        try
        {
            return Registry.GetValue(key, name, null) as string;
        }
        catch (SecurityException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static void Add(List<string> roots, string? path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;
        if (!roots.Contains(path, StringComparer.OrdinalIgnoreCase)) roots.Add(path);
    }
}

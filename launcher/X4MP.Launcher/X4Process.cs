using System.Diagnostics;

namespace X4MP.Launcher;

/// <summary>
/// Whether X4 is running. The game writes the profile's content.xml itself, and a loaded dll cannot be swapped, so
/// both applying and restoring wait until every window is gone. X4b is the second instance used for local testing.
/// </summary>
internal static class X4Process
{
    public static bool IsRunning() => HasProcess("X4") || HasProcess("X4b");

    private static bool HasProcess(string name)
    {
        Process[] found = Process.GetProcessesByName(name);
        try
        {
            return found.Length > 0;
        }
        finally
        {
            foreach (Process process in found)
            {
                process.Dispose();
            }
        }
    }
}

using System.Globalization;

namespace X4MP.Launcher.Core.Tests;

/// <summary>A throwaway folder for a test that builds a fake X4 or Steam tree and deletes it afterwards.</summary>
internal sealed class TempTree : IDisposable
{
    public TempTree()
    {
        Root = Path.Combine(Path.GetTempPath(), "x4mp-launcher-test-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    /// <summary>Creates (and returns) a folder below the root.</summary>
    public string Dir(params string[] parts)
    {
        var path = Path.Combine([Root, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Writes a text file below the root, creating its folder.</summary>
    public string Write(string relative, string text)
    {
        var path = Resolve(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>Creates an empty file below the root, for the files a scan only looks at by name.</summary>
    public string Touch(string relative)
    {
        var path = Resolve(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
        return path;
    }

    public string Resolve(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, true);
        }
        catch (IOException)
        {
            // A test that still holds a file open should not turn into a failure of the test that follows.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

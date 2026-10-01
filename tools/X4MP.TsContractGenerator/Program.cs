using X4MP.TsContract;

// Usage: dotnet run --project tools/X4MP.TsContractGenerator [output-path]
// Default output: server/web/src/generated/generated.ts, found by walking up from the current directory to X4MP.sln.
// (Explicit entry-point class: a top-level `Program` would clash with X4MP.Server's public Program in the test project.)
internal static class GeneratorEntryPoint
{
    private static int Main(string[] args)
    {
        var output = args.Length > 0 ? args[0] : Path.Combine(FindRepoRoot(), "server", "web", "src", "generated", "generated.ts");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, TsContractGenerator.Generate());
        Console.WriteLine($"Wrote {output}");
        return 0;
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "X4MP.sln")))
            {
                return dir.FullName;
            }
        }
        throw new InvalidOperationException("Run from inside the repository, or pass an output path.");
    }
}

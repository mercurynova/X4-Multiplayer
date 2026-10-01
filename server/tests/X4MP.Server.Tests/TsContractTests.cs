using X4MP.Server.Api;
using X4MP.TsContract;

namespace X4MP.Server.Tests;

public class TsContractTests
{
    [Fact]
    public void CommittedGeneratedTsMatchesTheDtos()
    {
        var path = Path.Combine(RepoRoot(), "server", "web", "src", "generated", "generated.ts");
        var committed = File.ReadAllText(path).ReplaceLineEndings("\n");
        var generated = TsContractGenerator.Generate();

        Assert.True(
            committed == generated,
            "server/web/src/generated/generated.ts is out of date. Regenerate with: " +
            "dotnet run --project tools/X4MP.TsContractGenerator");
    }

    [Fact]
    public void HealthzDtoIsInTheContract()
    {
        var generated = TsContractGenerator.Generate();
        Assert.Contains("export interface HealthzResponse {", generated, StringComparison.Ordinal);
        Assert.Contains("  uptimeSeconds: number;", generated, StringComparison.Ordinal);
    }

    [TsContract]
    private enum Color { Red, Green }

    [TsContract]
    private sealed record Sample(
        string Name,
        int? Count,
        string? Note,
        List<string> Tags,
        Dictionary<string, long> Totals,
        Color Color,
        Sample[]? Children,
        DateTimeOffset At);

    [Fact]
    public void MapsCommonShapes()
    {
        var generated = TsContractGenerator.Generate([typeof(Sample), typeof(Color)]);
        Assert.Contains("export type Color = 'Red' | 'Green';", generated, StringComparison.Ordinal);
        Assert.Contains("  name: string;\n", generated, StringComparison.Ordinal);
        Assert.Contains("  count: number | null;\n", generated, StringComparison.Ordinal);
        Assert.Contains("  note: string | null;\n", generated, StringComparison.Ordinal);
        Assert.Contains("  tags: string[];\n", generated, StringComparison.Ordinal);
        Assert.Contains("  totals: Record<string, number>;\n", generated, StringComparison.Ordinal);
        Assert.Contains("  color: Color;\n", generated, StringComparison.Ordinal);
        Assert.Contains("  children: Sample[] | null;\n", generated, StringComparison.Ordinal);
        Assert.Contains("  at: string;\n", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void UnmarkedReferencedTypeIsRejected() =>
        Assert.Throws<NotSupportedException>(() => TsContractGenerator.Generate([typeof(Bad)]));

    [TsContract]
    private sealed record Bad(Unmarked Other);

    private sealed record Unmarked(int X);

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "X4MP.sln")))
            {
                return dir.FullName;
            }
        }
        throw new InvalidOperationException("X4MP.sln not found above " + AppContext.BaseDirectory);
    }
}

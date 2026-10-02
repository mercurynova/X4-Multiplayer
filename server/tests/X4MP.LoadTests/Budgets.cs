using System.Globalization;
using System.Text.Json;

namespace X4MP.LoadTests;

/// <summary>One broken budget: which metric, its measured value and the limit it crossed.</summary>
public sealed record BudgetViolation(string Budget, string Metric, double Actual, double Limit, bool IsMaximum)
{
    public override string ToString() =>
        double.IsNaN(Actual)
            ? string.Create(CultureInfo.InvariantCulture, $"{Metric} was not measured but budget {Budget} = {Limit:0.###} needs it")
            : string.Create(CultureInfo.InvariantCulture, $"{Metric} = {Actual:0.###} is {(IsMaximum ? "above" : "below")} the budget {Budget} = {Limit:0.###}");
}

/// <summary>
/// The checked-in budgets (<c>budgets.json</c>): a flat map from <c>&lt;metric&gt;Max</c> / <c>&lt;metric&gt;Min</c> to a limit, checked against the
/// report's metrics map. Keys starting with an underscore are comments.
/// </summary>
public static class Budgets
{
    public static Dictionary<string, double> Load(string path)
    {
        using var stream = File.OpenRead(path);
        using var doc = JsonDocument.Parse(stream);
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            if (!p.Name.StartsWith('_') && p.Value.ValueKind == JsonValueKind.Number)
            {
                result[p.Name] = p.Value.GetDouble();
            }
        }

        return result;
    }

    /// <summary>Applies <c>key=value</c> overrides (a budget set artificially low, a one-off relaxation).</summary>
    public static void ApplyOverrides(Dictionary<string, double> budgets, IEnumerable<string> overrides)
    {
        foreach (var o in overrides)
        {
            int eq = o.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0 || !double.TryParse(o[(eq + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                throw new ArgumentException($"--budget expects key=value, got '{o}'");
            }

            budgets[o[..eq]] = value;
        }
    }

    public static List<BudgetViolation> Evaluate(IReadOnlyDictionary<string, double> budgets, IReadOnlyDictionary<string, double> metrics)
    {
        var violations = new List<BudgetViolation>();
        foreach (var (budget, limit) in budgets)
        {
            bool isMax = budget.EndsWith("Max", StringComparison.Ordinal);
            bool isMin = budget.EndsWith("Min", StringComparison.Ordinal);
            if (!isMax && !isMin)
            {
                throw new ArgumentException($"Budget '{budget}' must end in Max or Min");
            }

            string metric = budget[..^3];
            if (!metrics.TryGetValue(metric, out var actual))
            {
                // A budget for a metric the run did not produce can never pass: fail loudly rather than skip it.
                violations.Add(new BudgetViolation(budget, metric, double.NaN, limit, isMax));
            }
            else if (isMax ? actual > limit : actual < limit)
            {
                violations.Add(new BudgetViolation(budget, metric, actual, limit, isMax));
            }
        }

        return violations;
    }
}

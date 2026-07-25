using System.Diagnostics;

using JBU.CodeLens.Shared.Structural;

namespace JBU.CodeLens.Core.Analysis;

public static class MetricsCalculator
{
    public static MetricsResult Calculate(ProjectIR ir)
    {
        ArgumentNullException.ThrowIfNull(ir);

        var result = new MetricsResult
        {
            TotalClasses = ir.Classes.Count,
            TotalMethods = ir.Methods.Count,
            TotalNamespaces = ir.Namespaces.Count,
            TotalRelationships = ir.Relationships.Count,
        };

        foreach (var cls in ir.Classes)
        {
            result.TotalProperties += cls.Properties.Count;
            result.TotalFields += cls.Fields.Count;
        }

        result.AverageMethodsPerClass = ir.Classes.Count > 0
            ? Math.Round((double)result.TotalMethods / ir.Classes.Count, 2)
            : 0;
        result.AveragePropertiesPerClass = ir.Classes.Count > 0
            ? Math.Round((double)result.TotalProperties / ir.Classes.Count, 2)
            : 0;

        if (ir.Methods.Count > 0)
        {
            result.AverageComplexity = Math.Round(ir.Methods.Average(m => m.CyclomaticComplexity), 2);
            result.MaxComplexity = ir.Methods.Max(m => m.CyclomaticComplexity);
        }

        result.AverageCoupling = ir.Classes.Count > 0
            ? Math.Round((double)result.TotalRelationships / ir.Classes.Count, 2)
            : 0;

        result.MaxInheritanceDepth = CalculateMaxInheritanceDepth(ir);
        result.MaintainabilityIndex = CalculateMaintainabilityIndex(ir);

        // The maximum of a set can never be below its mean. This holds by construction today, but
        // both figures are aggregated separately over ir.Methods, so a future change to either
        // aggregation that silently disagrees with the other would surface here rather than as
        // quietly wrong numbers in the exported report.
        Debug.Assert(
            ir.Methods.Count == 0 || result.MaxComplexity >= result.AverageComplexity,
            $"MaxComplexity ({result.MaxComplexity}) is below AverageComplexity " +
            $"({result.AverageComplexity}) over {ir.Methods.Count} methods.");

        return result;
    }

    private static int CalculateMaxInheritanceDepth(ProjectIR ir)
    {
        // Index each type's single base once (first INHERITS wins, matching the previous
        // FirstOrDefault semantics). Without this, every step of every chain walk re-scanned
        // the entire relationship list — which also contains the far more numerous CALLS edges —
        // making the whole calculation O(classes × depth × relationships) on large projects.
        var baseByType = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rel in ir.Relationships)
        {
            if (rel.Kind == "INHERITS")
            {
                baseByType.TryAdd(rel.SourceId, rel.TargetId);
            }
        }

        var depth = 1;
        foreach (var cls in ir.Classes)
        {
            var currentDepth = 1;
            var current = cls.FullName;
            var visited = new HashSet<string>(StringComparer.Ordinal);

            // Add() returns false on a cycle (target already visited), ending the walk.
            while (baseByType.TryGetValue(current, out var baseType) && visited.Add(baseType))
            {
                currentDepth++;
                current = baseType;
            }

            if (currentDepth > depth)
                depth = currentDepth;
        }

        // Every type sits at depth 1 even with no base type, and the cycle guard above bounds the
        // walk. A zero or negative depth would mean the seed value or the guard had been broken.
        Debug.Assert(depth >= 1, $"Inheritance depth must be at least 1, got {depth}.");

        return depth;
    }

    /// <summary>
    /// Simplified maintainability index driven by average cyclomatic complexity and the fraction
    /// of classes carrying a documentation summary (a proxy for comment density, since the parser
    /// doesn't track raw line/comment counts).
    /// </summary>
    private static double CalculateMaintainabilityIndex(ProjectIR ir)
    {
        var avgComplexity = ir.Methods.Count > 0
            ? ir.Methods.Average(m => m.CyclomaticComplexity)
            : 1;
        var documentedRatio = ir.Classes.Count > 0
            ? ir.Classes.Count(c => c.Documentation is not null) / (double)ir.Classes.Count
            : 0;

        // A count of matching classes divided by the total can only land in [0, 1]. If it ever does
        // not, the numerator and denominator have drifted apart and the index below is meaningless.
        Debug.Assert(
            documentedRatio is >= 0 and <= 1,
            $"Documented-class ratio out of range: {documentedRatio}.");

        var mi = 171
            - 5.2 * Math.Log(Math.Max(1, avgComplexity))
            - 16.2 * Math.Log(Math.Max(1, documentedRatio * 100 + 1));

        return Math.Round(Math.Max(0, Math.Min(100, mi)), 2);
    }
}

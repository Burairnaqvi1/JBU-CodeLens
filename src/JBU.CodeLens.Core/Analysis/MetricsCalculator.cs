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
    /// How complex the code is set against how well it is described, as a score out of 100.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the project's own score, not the maintainability index published by Microsoft.
    /// That one needs Halstead volume, which is derived from operator and operand counts this
    /// parser does not gather, so it cannot be computed here honestly.
    /// </para>
    /// <para>
    /// An earlier attempt kept the shape of the published formula and fed the documented-class
    /// ratio into the term meant for lines of code. Because that term is subtracted, documenting
    /// the code <em>lowered</em> the score: a project with nothing documented scored 100 and one
    /// documented throughout scored 89. The clamp at 100 hid the rest of the problem, leaving the
    /// figure almost unmoved by complexity — an average of 100 still scored 83.
    /// </para>
    /// <para>
    /// The score is now built from the two things this parser does measure, each pointing the way
    /// round it should:
    /// </para>
    /// <list type="bullet">
    /// <item>Complexity, worth 70. Full marks at an average of 2 or below, nothing at 20 or above.</item>
    /// <item>Documentation, worth 30, in direct proportion to the share of classes carrying a summary.</item>
    /// </list>
    /// <para>
    /// Complexity carries the larger share because it is what actually makes code hard to change;
    /// documentation helps a reader but cannot rescue a tangled method.
    /// </para>
    /// </remarks>
    private static double CalculateMaintainabilityIndex(ProjectIR ir)
    {
        const double simpleEnough = 2;
        const double tooComplex = 20;
        const double complexityWeight = 70;
        const double documentationWeight = 30;

        var avgComplexity = ir.Methods.Count > 0
            ? ir.Methods.Average(m => m.CyclomaticComplexity)
            : simpleEnough;

        var documentedRatio = ir.Classes.Count > 0
            ? ir.Classes.Count(c => c.Documentation is not null) / (double)ir.Classes.Count
            : 0;

        // A count of matching classes divided by the total can only land in [0, 1]. If it ever does
        // not, the numerator and denominator have drifted apart and the score below is meaningless.
        Debug.Assert(
            documentedRatio is >= 0 and <= 1,
            $"Documented-class ratio out of range: {documentedRatio}.");

        var complexityHealth = Math.Clamp(
            (tooComplex - avgComplexity) / (tooComplex - simpleEnough), 0, 1);

        var score = (complexityHealth * complexityWeight) + (documentedRatio * documentationWeight);

        // Both parts are already bounded, so the total cannot leave 0..100. Asserted because the
        // figure is printed on the dashboard and in the exported report: a score outside the range
        // it claims would be read as fact.
        Debug.Assert(score is >= 0 and <= 100, $"Maintainability score out of range: {score}.");

        return Math.Round(score, 2);
    }
}

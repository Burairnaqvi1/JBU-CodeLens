using CodeLensAI.Shared.Structural;

namespace CodeLensAI.Core.Analysis;

public class MetricsCalculator
{
    public MetricsResult Calculate(ProjectIR ir)
    {
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

        return result;
    }

    private static int CalculateMaxInheritanceDepth(ProjectIR ir)
    {
        var depth = 1;
        foreach (var cls in ir.Classes)
        {
            var currentDepth = 1;
            var current = cls.FullName;
            var visited = new HashSet<string>();

            while (true)
            {
                var baseType = ir.Relationships
                    .FirstOrDefault(r => r.SourceId == current && r.Kind == "INHERITS");
                if (baseType == null || visited.Contains(baseType.TargetId))
                    break;
                visited.Add(baseType.TargetId);
                currentDepth++;
                current = baseType.TargetId;
            }

            if (currentDepth > depth)
                depth = currentDepth;
        }

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

        var mi = 171
            - 5.2 * Math.Log(Math.Max(1, avgComplexity))
            - 16.2 * Math.Log(Math.Max(1, documentedRatio * 100 + 1));

        return Math.Round(Math.Max(0, Math.Min(100, mi)), 2);
    }
}

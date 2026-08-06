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

        return result;
    }
}

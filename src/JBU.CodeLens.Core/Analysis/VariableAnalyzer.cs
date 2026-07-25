namespace JBU.CodeLens.Core.Analysis;

/// <summary>
/// Classifies parameters, locals, fields, and properties by read/write usage.
/// </summary>
public sealed class VariableAnalyzer
{
    private readonly RuleEngine<VariableAnalysis> _engine;

    public VariableAnalyzer()
    {
        _engine = new RuleEngine<VariableAnalysis>()
            .Register("track-parameters", "Parameter usage", RuleParameters)
            .Register("track-locals", "Local variable usage", RuleLocals)
            .Register("track-fields", "Class field usage", RuleFields)
            .Register("track-properties", "Property usage", RuleProperties);
    }

    public IReadOnlyList<VariableAnalysis> Analyze(MethodAnalysisContext context) =>
        _engine.EvaluateAll(context);

    public IReadOnlyList<AnalysisRule<VariableAnalysis>> Rules => _engine.Rules;

    private static IEnumerable<VariableAnalysis> RuleParameters(MethodAnalysisContext context)
    {
        foreach (var parameter in context.Method.Parameters)
        {
            var parts = parameter.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                continue;
            }

            var type = string.Join(" ", parts[..^1]);
            var name = parts[^1].Trim(',', '&', '*');
            var usage = ClassifySymbolUsage(context, name);

            yield return new VariableAnalysis
            {
                Name = name,
                Type = type,
                Scope = VariableScopeKind.Parameter,
                Usage = usage,
                Confidence = context.HasSourceBody ? AnalysisConfidence.High : AnalysisConfidence.Low,
                Reason = context.HasSourceBody
                    ? "Usage inferred from method body references."
                    : "No method body available; marked unused by default.",
            };
        }
    }

    private static IEnumerable<VariableAnalysis> RuleLocals(MethodAnalysisContext context)
    {
        foreach (var local in context.Method.LocalVariables)
        {
            var usage = ClassifySymbolUsage(context, local.Name);

            yield return new VariableAnalysis
            {
                Name = local.Name,
                Type = local.Type,
                Scope = VariableScopeKind.Local,
                Usage = usage,
                Confidence = context.HasSourceBody ? AnalysisConfidence.High : AnalysisConfidence.Medium,
                Reason = context.HasSourceBody
                    ? "Usage inferred from method body references."
                    : "Declared locally; body unavailable for usage scan.",
            };
        }
    }

    private static IEnumerable<VariableAnalysis> RuleFields(MethodAnalysisContext context)
    {
        var fields = context.Method.ParentClass?.Fields ?? [];
        foreach (var field in fields)
        {
            var usage = ClassifySymbolUsage(context, field.Name);

            yield return new VariableAnalysis
            {
                Name = field.Name,
                Type = field.Type,
                Scope = VariableScopeKind.Field,
                Usage = usage,
                Confidence = context.HasSourceBody ? AnalysisConfidence.High : AnalysisConfidence.Low,
                Reason = context.HasSourceBody
                    ? "Usage inferred from method body references."
                    : "No method body available; field usage unknown.",
            };
        }
    }

    private static IEnumerable<VariableAnalysis> RuleProperties(MethodAnalysisContext context)
    {
        var properties = context.Method.ParentClass?.Properties ?? [];
        foreach (var property in properties)
        {
            var usage = ClassifySymbolUsage(context, property.Name);

            yield return new VariableAnalysis
            {
                Name = property.Name,
                Type = property.Type,
                Scope = VariableScopeKind.Property,
                Usage = usage,
                Confidence = context.HasSourceBody ? AnalysisConfidence.High : AnalysisConfidence.Low,
                Reason = context.HasSourceBody
                    ? "Usage inferred from method body references."
                    : "No method body available; property usage unknown.",
            };
        }
    }

    private static VariableUsageKind ClassifySymbolUsage(MethodAnalysisContext context, string name)
    {
        if (!context.HasSourceBody)
        {
            return VariableUsageKind.Unused;
        }

        return SourcePatternHelpers.ClassifyUsage(context.SourceBody, name, isDeclared: true);
    }
}

using System.Text.RegularExpressions;

namespace JBU.CodeLens.Core.Analysis;

/// <summary>
/// Detects runtime risks using deterministic source patterns (no AI).
/// </summary>
public sealed class RuntimeRiskAnalyzer
{
    private readonly RuleEngine<RuntimeRisk> _engine;

    public RuntimeRiskAnalyzer()
    {
        _engine = new RuleEngine<RuntimeRisk>()
            .Register("divide-by-zero", "Division without visible guard", RuleDivideByZero)
            .Register("int-parse", "int.Parse usage", RuleIntParse)
            .Register("file-open", "File.Open usage", RuleFileOpen)
            .Register("parser-thrown", "Parser-detected thrown exceptions", RuleParserThrownExceptions)
            .Register("null-dereference", "Possible null dereference", RuleNullDereference);
    }

    public IReadOnlyList<RuntimeRisk> Analyze(MethodAnalysisContext context) =>
        _engine.EvaluateAll(context);

    public IReadOnlyList<AnalysisRule<RuntimeRisk>> Rules => _engine.Rules;

    private static IEnumerable<RuntimeRisk> RuleDivideByZero(MethodAnalysisContext context)
    {
        if (!context.HasSourceBody || !context.SourceBody.Contains('/', StringComparison.Ordinal))
        {
            yield break;
        }

        foreach (var parameter in SourcePatternHelpers.ExtractParameterNames(context.Method))
        {
            if (!IsUsedAsDivisor(context.SourceBody, parameter))
            {
                continue;
            }

            if (HasVisibleDivisorGuard(context.SourceBody, parameter))
            {
                continue;
            }

            yield return new RuntimeRisk
            {
                Description = $"Possible divide-by-zero when '{parameter}' is zero.",
                Confidence = AnalysisConfidence.Medium,
                Reason = "Division uses a parameter without a visible zero guard.",
                RuleId = "divide-by-zero",
            };
        }
    }

    private static IEnumerable<RuntimeRisk> RuleIntParse(MethodAnalysisContext context)
    {
        if (!context.HasSourceBody)
        {
            yield break;
        }

        if (!SafeRegex.IsMatch(context.SourceBody, @"\bint\.Parse\s*\("))
        {
            yield break;
        }

        yield return new RuntimeRisk
        {
            Description = "int.Parse may throw on invalid input.",
            ExceptionType = "FormatException",
            Confidence = AnalysisConfidence.High,
            Reason = "Derived from int.Parse call.",
            RuleId = "int-parse",
        };

        yield return new RuntimeRisk
        {
            Description = "int.Parse may overflow for out-of-range values.",
            ExceptionType = "OverflowException",
            Confidence = AnalysisConfidence.Medium,
            Reason = "Derived from int.Parse call.",
            RuleId = "int-parse",
        };
    }

    private static IEnumerable<RuntimeRisk> RuleFileOpen(MethodAnalysisContext context)
    {
        if (!context.HasSourceBody)
        {
            yield break;
        }

        if (!context.SourceBody.Contains("File.Open", StringComparison.Ordinal) &&
            !context.SourceBody.Contains("fopen(", StringComparison.Ordinal))
        {
            yield break;
        }

        yield return new RuntimeRisk
        {
            Description = "File open may fail if the path is missing or inaccessible.",
            ExceptionType = "IOException",
            Confidence = AnalysisConfidence.Medium,
            Reason = "Derived from file open API usage.",
            RuleId = "file-open",
        };

        yield return new RuntimeRisk
        {
            Description = "File open may be denied due to permissions.",
            ExceptionType = "UnauthorizedAccessException",
            Confidence = AnalysisConfidence.Medium,
            Reason = "Derived from file open API usage.",
            RuleId = "file-open",
        };

        yield return new RuntimeRisk
        {
            Description = "File open may fail when the file does not exist.",
            ExceptionType = "FileNotFoundException",
            Confidence = AnalysisConfidence.Medium,
            Reason = "Derived from file open API usage.",
            RuleId = "file-open",
        };
    }

    private static IEnumerable<RuntimeRisk> RuleParserThrownExceptions(MethodAnalysisContext context)
    {
        foreach (var exception in context.Method.ThrownExceptions)
        {
            yield return new RuntimeRisk
            {
                Description = $"Method may throw {exception}.",
                ExceptionType = exception,
                Confidence = AnalysisConfidence.High,
                Reason = "Derived from parser throw analysis.",
                RuleId = "parser-thrown",
            };
        }
    }

    private static IEnumerable<RuntimeRisk> RuleNullDereference(MethodAnalysisContext context)
    {
        if (!context.HasSourceBody)
        {
            yield break;
        }

        foreach (var parameter in SourcePatternHelpers.ExtractParameterNames(context.Method))
        {
            if ((context.SourceBody.Contains(parameter + ".", StringComparison.Ordinal) ||
                 context.SourceBody.Contains(parameter + "->", StringComparison.Ordinal)) &&
                !HasNullGuard(context.SourceBody, parameter))
            {
                yield return new RuntimeRisk
                {
                    Description = $"Possible null dereference on '{parameter}'.",
                    ExceptionType = context.Language == "C++" ? "undefined behavior" : "NullReferenceException",
                    Confidence = AnalysisConfidence.Low,
                    Reason = "Member access on parameter without visible null guard.",
                    RuleId = "null-dereference",
                };
            }
        }
    }

    private static bool IsUsedAsDivisor(string source, string identifier)
    {
        var pattern = $@"/\s*{Regex.Escape(identifier)}\b|/\s*\(\s*{Regex.Escape(identifier)}\s*\)";
        return SafeRegex.IsMatch(source, pattern);
    }

    private static bool HasVisibleDivisorGuard(string source, string identifier) =>
        source.Contains($"if ({identifier}", StringComparison.Ordinal) ||
        source.Contains($"if({identifier}", StringComparison.Ordinal) ||
        source.Contains($"{identifier} != 0", StringComparison.Ordinal) ||
        source.Contains($"{identifier} > 0", StringComparison.Ordinal);

    private static bool HasNullGuard(string source, string identifier) =>
        source.Contains($"{identifier} == null", StringComparison.Ordinal) ||
        source.Contains($"{identifier} != null", StringComparison.Ordinal) ||
        source.Contains($"{identifier} == nullptr", StringComparison.Ordinal) ||
        source.Contains($"ThrowIfNull({identifier}", StringComparison.Ordinal);
}

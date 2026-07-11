using System.Text.RegularExpressions;

namespace CodeLensAI.Core.Analysis;

/// <summary>
/// Extracts method calls, external APIs, namespaces, and referenced types.
/// </summary>
public sealed class DependencyAnalyzer
{
    private static readonly HashSet<string> IgnoredCallTargets = new(StringComparer.Ordinal)
    {
        "if", "for", "while", "switch", "catch", "return", "new", "sizeof", "typeof",
    };

    private readonly RuleEngine<DependencyInfo> _engine;

    public DependencyAnalyzer()
    {
        _engine = new RuleEngine<DependencyInfo>()
            .Register("method-calls", "Invoked methods", RuleMethodCalls)
            .Register("external-apis", "BCL / STL / external APIs", RuleExternalApis)
            .Register("class-dependencies", "Parser class dependencies", RuleParserClassDependencies)
            .Register("namespaces", "Namespace-like qualifiers", RuleNamespaces);
    }

    public IReadOnlyList<DependencyInfo> Analyze(MethodAnalysisContext context) =>
        _engine.EvaluateAll(context);

    public IReadOnlyList<AnalysisRule<DependencyInfo>> Rules => _engine.Rules;

    private static IEnumerable<DependencyInfo> RuleMethodCalls(MethodAnalysisContext context)
    {
        if (!context.HasSourceBody)
        {
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        const string pattern = @"\b([A-Za-z_][\w]*)\s*\(";
        foreach (Match match in Regex.Matches(context.SourceBody, pattern))
        {
            var name = match.Groups[1].Value;
            if (IgnoredCallTargets.Contains(name) || name == context.Method.Name || !seen.Add(name))
            {
                continue;
            }

            yield return new DependencyInfo
            {
                Name = name,
                Kind = DependencyKind.MethodCall,
                Confidence = AnalysisConfidence.Medium,
                Reason = "Derived from method invocation in source body.",
                RuleId = "method-calls",
            };
        }
    }

    private static IEnumerable<DependencyInfo> RuleExternalApis(MethodAnalysisContext context)
    {
        if (!context.HasSourceBody)
        {
            yield break;
        }

        var apiPatterns = new (string Pattern, string Name)[]
        {
            (@"\bConsole\.\w+", "Console"),
            (@"\bFile\.\w+", "System.IO.File"),
            (@"\bDirectory\.\w+", "System.IO.Directory"),
            (@"\bHttpClient\b", "System.Net.Http.HttpClient"),
            (@"\bSqlConnection\b", "System.Data.SqlClient.SqlConnection"),
            (@"\bstd::\w+", "std"),
            (@"\bfopen\s*\(", "fopen"),
            (@"\bprintf\s*\(", "printf"),
        };

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (pattern, name) in apiPatterns)
        {
            if (!Regex.IsMatch(context.SourceBody, pattern) || !seen.Add(name))
            {
                continue;
            }

            yield return new DependencyInfo
            {
                Name = name,
                Kind = DependencyKind.ExternalApi,
                Confidence = AnalysisConfidence.High,
                Reason = "Derived from external API usage in source body.",
                RuleId = "external-apis",
            };
        }
    }

    private static IEnumerable<DependencyInfo> RuleParserClassDependencies(MethodAnalysisContext context)
    {
        var dependencies = context.Method.ParentClass?.Dependencies ?? [];
        foreach (var dependency in dependencies)
        {
            yield return new DependencyInfo
            {
                Name = dependency,
                Kind = DependencyKind.ReferencedClass,
                Confidence = AnalysisConfidence.High,
                Reason = "Derived from parser class composition metadata.",
                RuleId = "class-dependencies",
            };
        }
    }

    private static IEnumerable<DependencyInfo> RuleNamespaces(MethodAnalysisContext context)
    {
        if (!context.HasSourceBody)
        {
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        const string pattern = @"\b([A-Za-z_][\w]*)\s*::\s*([A-Za-z_][\w]*)";
        foreach (Match match in Regex.Matches(context.SourceBody, pattern))
        {
            var qualifier = match.Groups[1].Value;
            if (!seen.Add(qualifier))
            {
                continue;
            }

            yield return new DependencyInfo
            {
                Name = qualifier,
                Kind = DependencyKind.Namespace,
                NamespaceOrType = qualifier,
                Confidence = AnalysisConfidence.Medium,
                Reason = "Derived from namespace or scope qualifier in source body.",
                RuleId = "namespaces",
            };
        }

        const string dottedPattern = @"\b([A-Za-z_][\w]*)\.([A-Za-z_][\w]*)\s*\(";
        foreach (Match match in Regex.Matches(context.SourceBody, dottedPattern))
        {
            var qualifier = match.Groups[1].Value;
            if (qualifier == "this" || !seen.Add(qualifier))
            {
                continue;
            }

            yield return new DependencyInfo
            {
                Name = qualifier,
                Kind = DependencyKind.Namespace,
                NamespaceOrType = qualifier,
                Confidence = AnalysisConfidence.Medium,
                Reason = "Derived from qualified call in source body.",
                RuleId = "namespaces",
            };
        }
    }
}

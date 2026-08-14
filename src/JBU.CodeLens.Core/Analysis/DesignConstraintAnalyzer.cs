using System.Text.RegularExpressions;

namespace JBU.CodeLens.Core.Analysis;

/// <summary>
/// Infers purity, statefulness, and external dependency constraints.
/// </summary>
public sealed class DesignConstraintAnalyzer
{
    private readonly RuleEngine<DesignConstraint> _engine;

    public DesignConstraintAnalyzer()
    {
        _engine = new RuleEngine<DesignConstraint>()
            .Register("uses-console", "Console / cout usage", RuleUsesConsole)
            .Register("uses-files", "File I/O usage", RuleUsesFiles)
            .Register("uses-database", "Database usage", RuleUsesDatabase)
            .Register("uses-network", "Network usage", RuleUsesNetwork)
            .Register("uses-static", "Static member access", RuleUsesStaticMembers)
            .Register("uses-recursion", "Self-recursive call", RuleUsesRecursion)
            .Register("uses-sync", "Thread synchronization", RuleUsesThreadSynchronization)
            .Register("stateful", "Mutates fields or collections", RuleStateful)
            .Register("pure-impure", "Pure vs impure summary", RulePureImpure);
    }

    public IReadOnlyList<DesignConstraint> Analyze(MethodAnalysisContext context) =>
        _engine.EvaluateAll(context);

    public IReadOnlyList<AnalysisRule<DesignConstraint>> Rules => _engine.Rules;

    private static IEnumerable<DesignConstraint> RuleUsesConsole(MethodAnalysisContext context)
    {
        if (!context.HasSourceBody)
        {
            yield break;
        }

        if (context.SourceBody.Contains("Console.", StringComparison.Ordinal) ||
            context.SourceBody.Contains("std::cout", StringComparison.Ordinal))
        {
            yield return Create(DesignConstraintKind.UsesConsole, "Uses console output.", "uses-console");
        }
    }

    private static IEnumerable<DesignConstraint> RuleUsesFiles(MethodAnalysisContext context)
    {
        if (!context.HasSourceBody)
        {
            yield break;
        }

        if (context.SourceBody.Contains("File.", StringComparison.Ordinal) ||
            context.SourceBody.Contains("fstream", StringComparison.Ordinal) ||
            context.SourceBody.Contains("ifstream", StringComparison.Ordinal) ||
            context.SourceBody.Contains("ofstream", StringComparison.Ordinal))
        {
            yield return Create(DesignConstraintKind.UsesFiles, "Uses file I/O.", "uses-files");
        }
    }

    private static IEnumerable<DesignConstraint> RuleUsesDatabase(MethodAnalysisContext context)
    {
        if (!context.HasSourceBody)
        {
            yield break;
        }

        if (SafeRegex.IsMatch(context.SourceBody, @"\b(SqlConnection|DbContext|ExecuteNonQuery|SaveChanges)\b"))
        {
            yield return Create(DesignConstraintKind.UsesDatabase, "Uses database APIs.", "uses-database");
        }
    }

    private static IEnumerable<DesignConstraint> RuleUsesNetwork(MethodAnalysisContext context)
    {
        if (!context.HasSourceBody)
        {
            yield break;
        }

        if (context.SourceBody.Contains("HttpClient", StringComparison.Ordinal) ||
            context.SourceBody.Contains("WebRequest", StringComparison.Ordinal) ||
            context.SourceBody.Contains("socket(", StringComparison.Ordinal))
        {
            yield return Create(DesignConstraintKind.UsesNetwork, "Uses network APIs.", "uses-network");
        }
    }

    private static IEnumerable<DesignConstraint> RuleUsesStaticMembers(MethodAnalysisContext context)
    {
        if (!context.HasSourceBody)
        {
            yield break;
        }

        if (SafeRegex.IsMatch(context.SourceBody, @"\b[A-Z][A-Za-z0-9_]*\.[A-Z][A-Za-z0-9_]*\s*\("))
        {
            yield return Create(
                DesignConstraintKind.UsesStaticMembers,
                "Calls static members on a type.",
                "uses-static",
                AnalysisConfidence.Medium);
        }
    }

    private static IEnumerable<DesignConstraint> RuleUsesRecursion(MethodAnalysisContext context)
    {
        if (!context.HasSourceBody)
        {
            yield break;
        }

        var methodName = context.Method.Name;
        if (string.IsNullOrEmpty(methodName))
        {
            yield break;
        }

        var escaped = Regex.Escape(methodName);
        var body = context.SourceBody;

        // Without a body there is no call to find, and the only occurrence of the name is the
        // declaration itself. Claiming recursion from that is claiming it from nothing.
        if (!body.Contains('{', StringComparison.Ordinal))
        {
            yield break;
        }

        // The C++ parser records a function's declaration along with its body, so the signature
        // itself matched "name(" and every C++ function was reported as recursive — the rule fired
        // on the definition rather than on a call. Anything ahead of the opening brace is the
        // declaration, so it is dropped before looking for a call, but only when it really is the
        // signature; C# bodies arrive already brace-first and are left untouched.
        var brace = body.IndexOf('{', StringComparison.Ordinal);
        if (brace > 0 && SafeRegex.IsMatch(body[..brace], $@"\b{escaped}\s*\("))
        {
            body = body[(brace + 1)..];
        }

        if (SafeRegex.IsMatch(body, $@"\b{escaped}\s*\("))
        {
            yield return Create(DesignConstraintKind.UsesRecursion, "Calls itself recursively.", "uses-recursion");
        }
    }

    private static IEnumerable<DesignConstraint> RuleUsesThreadSynchronization(MethodAnalysisContext context)
    {
        if (!context.HasSourceBody)
        {
            yield break;
        }

        if (context.SourceBody.Contains("lock (", StringComparison.Ordinal) ||
            context.SourceBody.Contains("Monitor.", StringComparison.Ordinal) ||
            context.SourceBody.Contains("std::mutex", StringComparison.Ordinal) ||
            context.SourceBody.Contains("std::lock_guard", StringComparison.Ordinal))
        {
            yield return Create(
                DesignConstraintKind.UsesThreadSynchronization,
                "Uses thread synchronization.",
                "uses-sync");
        }
    }

    private static IEnumerable<DesignConstraint> RuleStateful(MethodAnalysisContext context)
    {
        if (!context.HasSourceBody)
        {
            yield break;
        }

        var fields = context.Method.ParentClass?.Fields ?? [];
        foreach (var field in fields)
        {
            if (SourcePatternHelpers.IsWrittenInSource(context.SourceBody, field.Name))
            {
                yield return Create(DesignConstraintKind.Stateful, "Mutates instance or class state.", "stateful");
                yield break;
            }
        }

        if (SafeRegex.IsMatch(context.SourceBody, @"\.(Add|Remove|Push_back|Insert|Erase)\s*\("))
        {
            yield return Create(DesignConstraintKind.Stateful, "Mutates a collection.", "stateful");
        }
    }

    private static IEnumerable<DesignConstraint> RulePureImpure(MethodAnalysisContext context)
    {
        var isImpure = DetectImpureBehavior(context);

        if (isImpure)
        {
            yield return Create(DesignConstraintKind.Impure, "Method has observable side effects.", "pure-impure");
        }
        else
        {
            yield return Create(
                DesignConstraintKind.Pure,
                context.HasSourceBody
                    ? "No side effects detected from current rules."
                    : "Assumed pure; no method body available for side-effect scan.",
                "pure-impure",
                context.HasSourceBody ? AnalysisConfidence.Medium : AnalysisConfidence.Low);

            yield return Create(
                DesignConstraintKind.Stateless,
                "Does not modify tracked fields or collections.",
                "pure-impure",
                context.HasSourceBody ? AnalysisConfidence.Medium : AnalysisConfidence.Low);
        }
    }

    private static bool DetectImpureBehavior(MethodAnalysisContext context)
    {
        if (!context.HasSourceBody)
        {
            return false;
        }

        if (context.SourceBody.Contains("Console.", StringComparison.Ordinal) ||
            context.SourceBody.Contains("std::cout", StringComparison.Ordinal) ||
            context.SourceBody.Contains("File.", StringComparison.Ordinal) ||
            context.SourceBody.Contains("fstream", StringComparison.Ordinal) ||
            context.SourceBody.Contains("HttpClient", StringComparison.Ordinal) ||
            SafeRegex.IsMatch(context.SourceBody, @"\b(SqlConnection|DbContext|ExecuteNonQuery|SaveChanges)\b"))
        {
            return true;
        }

        var fields = context.Method.ParentClass?.Fields ?? [];
        foreach (var field in fields)
        {
            if (SourcePatternHelpers.IsWrittenInSource(context.SourceBody, field.Name))
            {
                return true;
            }
        }

        return SafeRegex.IsMatch(context.SourceBody, @"\.(Add|Remove|Push_back|Insert|Erase)\s*\(");
    }

    private static DesignConstraint Create(
        DesignConstraintKind kind,
        string description,
        string ruleId,
        AnalysisConfidence confidence = AnalysisConfidence.High) =>
        new()
        {
            Kind = kind,
            Description = description,
            Confidence = confidence,
            Reason = "Derived from deterministic design-constraint rule.",
            RuleId = ruleId,
        };
}

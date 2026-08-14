using System.Text.RegularExpressions;

namespace JBU.CodeLens.Core.Analysis;

/// <summary>
/// Infers preconditions from guard clauses and validation helpers in method source.
/// </summary>
public sealed class PreconditionAnalyzer
{
    private readonly RuleEngine<MethodPrecondition> _engine;

    public PreconditionAnalyzer()
    {
        _engine = new RuleEngine<MethodPrecondition>()
            .Register("guard-eq-zero", "if (x == 0) throw", RuleGuardEqualZero)
            .Register("guard-null", "if (x == null) throw", RuleGuardNullCheck)
            .Register("guard-cpp-null", "if (x == nullptr) throw", RuleGuardCppNullCheck)
            .Register("guard-lt-zero", "if (x < 0) throw", RuleGuardLessThanZero)
            .Register("guard-lte-zero", "if (x <= 0) throw", RuleGuardLessThanOrEqualZero)
            .Register("guard-empty-string", "if (x == \"\") throw", RuleGuardEmptyString)
            .Register("throw-if-null", "ThrowIfNull(parameter)", RuleThrowIfNull)
            .Register("param-type-numeric", "Floating-point parameter constraints", RuleParameterNumericTypes)
            .Register("param-type-string", "String parameter without null guard", RuleParameterStringTypes)
            .Register("param-type-index", "Integer parameter used as index", RuleParameterIndexUsage)
            .Register("param-type-collection", "Collection parameter iteration", RuleParameterCollectionTypes)
            .Register("param-sqrt", "Square root argument must be non-negative", RuleSqrtParameter)
            .Register("param-file-io", "File I/O path accessibility", RuleFileIoPrecondition)
            .Register("parser-operational-limit", "Parser guard clauses", RuleParserOperationalLimits)
            .Register("guard-range-check", "Range validation", RuleGuardRangeCheck)
            .Register("param-bool-flag", "Boolean flag parameter", RuleParameterBoolFlag)
            .Register("param-enum-type", "Enum parameter", RuleParameterEnumType)
            .Register("guard-string-length", "String length check", RuleGuardStringLength)
            .Register("guard-count-check", "Collection count check", RuleGuardCountCheck)
            .Register("param-object-type", "Object/class parameter", RuleParameterObjectType)
            .Register("guard-try-pattern", "Try/catch around full body", RuleTryCatchWrapped);
    }

    /// <summary>
    /// Runs every registered rule and returns the findings with duplicates removed.
    /// </summary>
    /// <remarks>
    /// This is the single place duplicates are removed. Several rules match the same parameter more
    /// than once — the range rule alone has five patterns, of which three fire on a typical bounds
    /// check — and rules can also overlap with each other. Deduplicating here, keyed on subject and
    /// description, covers both cases; individual rules deliberately do not filter their own output.
    /// </remarks>
    public IReadOnlyList<MethodPrecondition> Analyze(MethodAnalysisContext context) =>
        AnalysisMessageBuilder.DeduplicatePreconditions(_engine.EvaluateAll(context));

    public IReadOnlyList<AnalysisRule<MethodPrecondition>> Rules => _engine.Rules;

    private static IEnumerable<MethodPrecondition> RuleGuardEqualZero(MethodAnalysisContext context)
    {
        if (!context.HasSourceBody)
        {
            yield break;
        }

        var parameterNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, parameterName) in SourcePatternHelpers.ParseParameters(context.Method))
        {
            parameterNames.Add(parameterName);
        }

        const string pattern = @"if\s*\(\s*([A-Za-z_][\w]*)\s*==\s*0\s*\)";
        foreach (var match in SourcePatternHelpers.MatchGuardThrows(context.SourceBody, pattern))
        {
            var name = match.Groups[1].Value;
            var usedAsDivisor = SourcePatternHelpers.IsUsedAsDivisor(context.SourceBody, name);
            yield return CreatePrecondition(
                name,
                AnalysisMessageBuilder.GuardZero(name, usedAsDivisor, parameterNames.Contains(name)),
                "guard-eq-zero");
        }
    }

    private static IEnumerable<MethodPrecondition> RuleGuardNullCheck(MethodAnalysisContext context)
    {
        if (!context.HasSourceBody)
        {
            yield break;
        }

        const string pattern = @"if\s*\(\s*([A-Za-z_][\w]*)\s*==\s*null\s*\)";
        foreach (var match in SourcePatternHelpers.MatchGuardThrows(context.SourceBody, pattern))
        {
            var name = match.Groups[1].Value;
            yield return CreatePrecondition(name, AnalysisMessageBuilder.GuardNull(name), "guard-null");
        }
    }

    private static IEnumerable<MethodPrecondition> RuleGuardCppNullCheck(MethodAnalysisContext context)
    {
        if (!context.HasSourceBody)
        {
            yield break;
        }

        const string pattern = @"if\s*\(\s*([A-Za-z_][\w]*)\s*==\s*nullptr\s*\)";
        foreach (var match in SourcePatternHelpers.MatchGuardThrows(context.SourceBody, pattern))
        {
            var name = match.Groups[1].Value;
            yield return CreatePrecondition(name, AnalysisMessageBuilder.GuardNull(name), "guard-cpp-null");
        }
    }

    private static IEnumerable<MethodPrecondition> RuleGuardLessThanZero(MethodAnalysisContext context)
    {
        if (!context.HasSourceBody)
        {
            yield break;
        }

        const string pattern = @"if\s*\(\s*([A-Za-z_][\w]*)\s*<\s*0\s*\)";
        foreach (var match in SourcePatternHelpers.MatchGuardThrows(context.SourceBody, pattern))
        {
            var name = match.Groups[1].Value;
            yield return CreatePrecondition(name, AnalysisMessageBuilder.GuardPositive(name), "guard-lt-zero");
        }
    }

    private static IEnumerable<MethodPrecondition> RuleGuardLessThanOrEqualZero(MethodAnalysisContext context)
    {
        if (!context.HasSourceBody)
        {
            yield break;
        }

        const string pattern = @"if\s*\(\s*([A-Za-z_][\w]*)\s*<=\s*0\s*\)";
        foreach (var match in SourcePatternHelpers.MatchGuardThrows(context.SourceBody, pattern))
        {
            var name = match.Groups[1].Value;
            yield return CreatePrecondition(name, AnalysisMessageBuilder.GuardNonPositive(name), "guard-lte-zero");
        }
    }

    private static IEnumerable<MethodPrecondition> RuleGuardEmptyString(MethodAnalysisContext context)
    {
        if (!context.HasSourceBody)
        {
            yield break;
        }

        var patterns = new[]
        {
            @"if\s*\(\s*([A-Za-z_][\w]*)\s*==\s*""""\s*\)",
            @"if\s*\(\s*([A-Za-z_][\w]*)\s*==\s*string\.Empty\s*\)",
            @"if\s*\(\s*string\.IsNullOrEmpty\s*\(\s*([A-Za-z_][\w]*)\s*\)\s*\)",
            @"if\s*\(\s*string\.IsNullOrWhiteSpace\s*\(\s*([A-Za-z_][\w]*)\s*\)\s*\)",
            @"if\s*\(\s*([A-Za-z_][\w]*)\.empty\s*\(\s*\)\s*\)",
        };

        foreach (var pattern in patterns)
        {
            foreach (var match in SourcePatternHelpers.MatchGuardThrows(context.SourceBody, pattern))
            {
                var name = match.Groups[1].Value;
                yield return CreatePrecondition(name, AnalysisMessageBuilder.GuardNullOrEmpty(name), "guard-empty-string");
            }
        }
    }

    private static IEnumerable<MethodPrecondition> RuleThrowIfNull(MethodAnalysisContext context)
    {
        if (!context.HasSourceBody)
        {
            yield break;
        }

        const string pattern =
            @"\b(?:ArgumentNullException\.)?ThrowIfNull(?:OrEmpty|OrWhiteSpace)?\s*\(\s*([A-Za-z_][\w]*)\s*\)";
        foreach (Match match in SafeRegex.Matches(context.SourceBody, pattern))
        {
            var name = match.Groups[1].Value;
            var message = match.Value.Contains("OrEmpty", StringComparison.Ordinal) ||
                          match.Value.Contains("OrWhiteSpace", StringComparison.Ordinal)
                ? AnalysisMessageBuilder.GuardNullOrEmpty(name)
                : AnalysisMessageBuilder.GuardNull(name);

            yield return CreatePrecondition(name, message, "throw-if-null");
        }
    }

    private static IEnumerable<MethodPrecondition> RuleParameterNumericTypes(MethodAnalysisContext context)
    {
        foreach (var (type, name) in SourcePatternHelpers.ParseParameters(context.Method))
        {
            if (!SourcePatternHelpers.IsFloatingType(type))
            {
                continue;
            }

            yield return CreatePrecondition(
                name,
                AnalysisMessageBuilder.NumericFinite(name),
                "param-type-numeric",
                AnalysisConfidence.Medium);
        }
    }

    private static IEnumerable<MethodPrecondition> RuleParameterStringTypes(MethodAnalysisContext context)
    {
        if (!context.HasSourceBody)
        {
            yield break;
        }

        foreach (var (type, name) in SourcePatternHelpers.ParseParameters(context.Method))
        {
            if (!SourcePatternHelpers.IsStringType(type))
            {
                continue;
            }

            if (SourcePatternHelpers.HasNullGuard(context.SourceBody, name))
            {
                continue;
            }

            yield return CreatePrecondition(
                name,
                AnalysisMessageBuilder.StringNotNullOrEmpty(name),
                "param-type-string",
                AnalysisConfidence.Medium);
        }
    }

    private static IEnumerable<MethodPrecondition> RuleParameterIndexUsage(MethodAnalysisContext context)
    {
        if (!context.HasSourceBody)
        {
            yield break;
        }

        foreach (var (type, name) in SourcePatternHelpers.ParseParameters(context.Method))
        {
            if (!SourcePatternHelpers.IsNumericType(type) || !SourcePatternHelpers.IsUsedAsIndex(context.SourceBody, name))
            {
                continue;
            }

            yield return CreatePrecondition(
                name,
                AnalysisMessageBuilder.IndexBounds(name),
                "param-type-index",
                AnalysisConfidence.Medium);
        }
    }

    private static IEnumerable<MethodPrecondition> RuleParameterCollectionTypes(MethodAnalysisContext context)
    {
        if (!context.HasSourceBody)
        {
            yield break;
        }

        foreach (var (type, name) in SourcePatternHelpers.ParseParameters(context.Method))
        {
            if (!SourcePatternHelpers.IsCollectionType(type))
            {
                continue;
            }

            if (!SourcePatternHelpers.MethodIteratesParameter(context.SourceBody, name))
            {
                continue;
            }

            yield return CreatePrecondition(
                name,
                AnalysisMessageBuilder.CollectionNotEmpty(name),
                "param-type-collection",
                AnalysisConfidence.Medium);
        }
    }

    private static IEnumerable<MethodPrecondition> RuleSqrtParameter(MethodAnalysisContext context)
    {
        if (!context.HasSourceBody)
        {
            yield break;
        }

        foreach (var (_, name) in SourcePatternHelpers.ParseParameters(context.Method))
        {
            if (!SourcePatternHelpers.HasSqrtUsage(context.SourceBody, name))
            {
                continue;
            }

            yield return CreatePrecondition(
                name,
                AnalysisMessageBuilder.SqrtNonNegative(name),
                "param-sqrt",
                AnalysisConfidence.High);
        }
    }

    private static IEnumerable<MethodPrecondition> RuleFileIoPrecondition(MethodAnalysisContext context)
    {
        if (!context.HasSourceBody || !SourcePatternHelpers.HasFileIoOperations(context.SourceBody))
        {
            yield break;
        }

        yield return CreatePrecondition(
            null,
            AnalysisMessageBuilder.FilePathAccessible(),
            "param-file-io",
            AnalysisConfidence.Medium);
    }

    private static IEnumerable<MethodPrecondition> RuleParserOperationalLimits(MethodAnalysisContext context)
    {
        foreach (var limit in context.Method.OperationalLimits)
        {
            var description = OperationalLimitFormatter.Format(limit, context);
            if (string.IsNullOrWhiteSpace(description))
            {
                continue;
            }

            yield return new MethodPrecondition
            {
                Subject = ExtractSubjectFromDescription(description),
                Description = description,
                Confidence = AnalysisConfidence.High,
                Reason = "Derived from parser guard clause metadata.",
                RuleId = "parser-operational-limit",
            };
        }
    }

    private static IEnumerable<MethodPrecondition> RuleGuardRangeCheck(MethodAnalysisContext context)
    {
        if (!context.HasSourceBody)
        {
            yield break;
        }

        var patterns = new[]
        {
            @"if\s*\(\s*([A-Za-z_][\w]*)\s*<\s*[^)]+\)",
            @"if\s*\(\s*([A-Za-z_][\w]*)\s*>\s*[^)]+\)",
            @"if\s*\(\s*([A-Za-z_][\w]*)\s*<=\s*[^)]+\)",
            @"if\s*\(\s*([A-Za-z_][\w]*)\s*>=\s*[^)]+\)",
            @"if\s*\(\s*([A-Za-z_][\w]*)\s*<\s*[^|]+\|\|\s*\1\s*>\s*[^)]+\)",
        };

        // The patterns capture whatever identifier precedes a comparison, and the sentence they
        // produce calls it a parameter. Neither assumption is checked by the regex: in C++,
        // `static_cast<size_t>(size)` presents its template bracket as a "<", so the cast operator
        // was reported as a parameter that callers must keep in range, and a local computed inside
        // the method was described as something the caller passes. Emitting only for names that
        // really are parameters removes both.
        var parameterNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, parameterName) in SourcePatternHelpers.ParseParameters(context.Method))
        {
            parameterNames.Add(parameterName);
        }

        foreach (var pattern in patterns)
        {
            foreach (var match in SourcePatternHelpers.MatchGuardThrows(context.SourceBody, pattern))
            {
                var name = match.Groups[1].Value;
                if (!parameterNames.Contains(name))
                {
                    continue;
                }

                yield return CreatePrecondition(
                    name,
                    $"Parameter {name} must be within the valid range accepted by this method",
                    "guard-range-check");
            }
        }
    }

    private static IEnumerable<MethodPrecondition> RuleParameterBoolFlag(MethodAnalysisContext context)
    {
        foreach (var (type, name) in SourcePatternHelpers.ParseParameters(context.Method))
        {
            if (!type.Equals("bool", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return CreatePrecondition(
                name,
                $"Parameter {name} is a boolean flag that controls method behavior",
                "param-bool-flag",
                AnalysisConfidence.Low);
        }
    }

    private static IEnumerable<MethodPrecondition> RuleParameterEnumType(MethodAnalysisContext context)
    {
        foreach (var (type, name) in SourcePatternHelpers.ParseParameters(context.Method))
        {
            if (!IsEnumLikeType(type))
            {
                continue;
            }

            var simpleType = GetSimpleTypeName(type);
            yield return CreatePrecondition(
                name,
                $"Parameter {name} must be a valid {simpleType} value recognized by this method",
                "param-enum-type",
                AnalysisConfidence.Medium);
        }
    }

    private static IEnumerable<MethodPrecondition> RuleGuardStringLength(MethodAnalysisContext context)
    {
        if (!context.HasSourceBody)
        {
            yield break;
        }

        var patterns = new[]
        {
            @"if\s*\(\s*([A-Za-z_][\w]*)\.Length\s*==\s*0\s*\)",
            @"if\s*\(\s*([A-Za-z_][\w]*)\.Length\s*<\s*\d+\s*\)",
            @"if\s*\(\s*([A-Za-z_][\w]*)\.length\s*\(\s*\)\s*==\s*0\s*\)",
            @"if\s*\(\s*([A-Za-z_][\w]*)\.length\s*\(\s*\)\s*<\s*\d+\s*\)",
            @"if\s*\(\s*([A-Za-z_][\w]*)\.size\s*\(\s*\)\s*==\s*0\s*\)",
        };

        foreach (var pattern in patterns)
        {
            foreach (var match in SourcePatternHelpers.MatchGuardThrows(context.SourceBody, pattern))
            {
                var name = match.Groups[1].Value;
                yield return CreatePrecondition(
                    name,
                    $"Parameter {name} must not be empty — minimum length is required",
                    "guard-string-length");
            }
        }
    }

    private static IEnumerable<MethodPrecondition> RuleGuardCountCheck(MethodAnalysisContext context)
    {
        if (!context.HasSourceBody)
        {
            yield break;
        }

        foreach (var (type, name) in SourcePatternHelpers.ParseParameters(context.Method))
        {
            if (!SourcePatternHelpers.IsCollectionType(type))
            {
                continue;
            }

            var escaped = Regex.Escape(name);
            var patterns = new[]
            {
                $@"if\s*\(\s*{escaped}\.Count\s*==\s*0\s*\)",
                $@"if\s*\(\s*{escaped}\.size\s*\(\s*\)\s*==\s*0\s*\)",
                $@"if\s*\(\s*{escaped}\.Length\s*==\s*0\s*\)",
            };

            foreach (var pattern in patterns)
            {
                if (!SourcePatternHelpers.MatchGuardThrows(context.SourceBody, pattern).Any())
                {
                    continue;
                }

                yield return CreatePrecondition(
                    name,
                    $"Parameter {name} must contain at least one element before calling this method",
                    "guard-count-check");
                break;
            }
        }
    }

    private static IEnumerable<MethodPrecondition> RuleParameterObjectType(MethodAnalysisContext context)
    {
        if (!context.HasSourceBody)
        {
            yield break;
        }

        foreach (var (type, name) in SourcePatternHelpers.ParseParameters(context.Method))
        {
            if (!IsObjectClassType(type) || SourcePatternHelpers.HasNullGuard(context.SourceBody, name))
            {
                continue;
            }

            var simpleType = GetSimpleTypeName(type);
            yield return CreatePrecondition(
                name,
                $"Parameter {name} must be a properly initialized {simpleType} instance",
                "param-object-type",
                AnalysisConfidence.Low);
        }
    }

    private static IEnumerable<MethodPrecondition> RuleTryCatchWrapped(MethodAnalysisContext context)
    {
        if (!context.HasSourceBody || !IsBodyWrappedInTryCatch(context.SourceBody))
        {
            yield break;
        }

        yield return CreatePrecondition(
            null,
            "This method handles exceptions internally — callers do not need to wrap it in try-catch",
            "guard-try-pattern",
            AnalysisConfidence.Medium);
    }

    private static MethodPrecondition CreatePrecondition(
        string? subject,
        string description,
        string ruleId,
        AnalysisConfidence confidence = AnalysisConfidence.High) =>
        new()
        {
            Subject = subject,
            Description = description,
            Confidence = confidence,
            Reason = "Derived from explicit guard clause or parameter rule.",
            RuleId = ruleId,
        };

    private static string? ExtractSubjectFromDescription(string description)
    {
        var match = SafeRegex.Match(description, @"Parameter\s+(\w+)\s+");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static bool IsEnumLikeType(string type)
    {
        var simple = GetSimpleTypeName(type);
        if (string.IsNullOrEmpty(simple) ||
            simple.Contains('<', StringComparison.Ordinal) ||
            simple.EndsWith("[]", StringComparison.Ordinal) ||
            !char.IsUpper(simple[0]))
        {
            return false;
        }

        return !IsPrimitiveOrBuiltInType(simple) &&
               !SourcePatternHelpers.IsCollectionType(type) &&
               !SourcePatternHelpers.IsStringType(type);
    }

    private static bool IsObjectClassType(string type)
    {
        var simple = GetSimpleTypeName(type);
        if (string.IsNullOrEmpty(simple) ||
            simple.Contains('<', StringComparison.Ordinal) ||
            simple.EndsWith("[]", StringComparison.Ordinal) ||
            !char.IsUpper(simple[0]))
        {
            return false;
        }

        return !IsPrimitiveOrBuiltInType(simple) &&
               !SourcePatternHelpers.IsCollectionType(type);
    }

    private static bool IsPrimitiveOrBuiltInType(string type)
    {
        return type is "bool" or "byte" or "sbyte" or "char" or "decimal" or "double" or "float" or
               "int" or "uint" or "long" or "ulong" or "short" or "ushort" or "void" or "object" or
               "string" or "String" or "size_t" or "IntPtr" or "UIntPtr";
    }

    private static string GetSimpleTypeName(string type) =>
        TypeNames.StripQualifiers(type.Trim().TrimEnd('&', '*'));

    private static bool IsBodyWrappedInTryCatch(string source)
    {
        var trimmed = source.Trim();
        if (!SafeRegex.IsMatch(trimmed, @"^try\s*\{", RegexOptions.IgnoreCase))
        {
            return false;
        }

        return SafeRegex.IsMatch(trimmed, @"\bcatch\s*\(", RegexOptions.IgnoreCase);
    }
}

using System.Text.RegularExpressions;

using JBU.CodeLens.Core.Utilities;

namespace JBU.CodeLens.Core.Analysis;

/// <summary>
/// Human-readable sentence templates for inference findings (8–25 words, no trailing period).
/// </summary>
internal static class AnalysisMessageBuilder
{
    internal static string GuardZero(string parameterName, bool usedAsDivisor) =>
        usedAsDivisor
            ? $"Parameter {parameterName} must not be zero — division by zero will throw an exception"
            : $"Parameter {parameterName} must not be zero — method throws if zero is passed";

    internal static string GuardNull(string parameterName) =>
        $"Parameter {parameterName} must not be null before calling this method";

    internal static string GuardNullOrEmpty(string parameterName) =>
        $"Parameter {parameterName} must not be null or empty before calling this method";

    internal static string GuardPositive(string parameterName) =>
        $"Parameter {parameterName} must be a positive value — negative values are rejected";

    internal static string GuardNonPositive(string parameterName) =>
        $"Parameter {parameterName} must be greater than zero — zero or negative values are rejected";

    internal static string NumericFinite(string parameterName) =>
        $"Parameter {parameterName} should be a finite numeric value — NaN and Infinity may produce unexpected results";

    internal static string StringNotNullOrEmpty(string parameterName) =>
        $"Parameter {parameterName} should not be null or empty when passed into this method";

    internal static string IndexBounds(string parameterName) =>
        $"Parameter {parameterName} must be within valid index bounds for the accessed collection";

    internal static string CollectionNotEmpty(string parameterName) =>
        $"Parameter {parameterName} must not be null and should contain at least one element if iterated";

    internal static string SqrtNonNegative(string parameterName) =>
        $"Parameter {parameterName} passed to square root should be non-negative — negative values produce NaN";

    internal static string FilePathAccessible() =>
        "File path parameter must point to an accessible file — throws if file does not exist or access is denied";

    internal static string PostVoidAction() =>
        "This method performs an action and returns no value";

    internal static string PostBoolResult() =>
        "Returns true if the operation succeeded, false otherwise";

    internal static string PostNumericResult(string returnType) =>
        $"Returns a numeric result as {NormalizeTypeName(returnType)}";

    internal static string PostStringResult() =>
        "Returns a text value — may return null if no value is found";

    internal static string PostCollectionResult() =>
        "Returns a collection — may be empty but not null";

    internal static string PostCountOrGetInt() =>
        "Returns a count or retrieved integer value";

    internal static string PostDivide(string first, string second, string returnType) =>
        $"Returns the result of dividing {first} by {second} as a {NormalizeTypeName(returnType)} value";

    internal static string PostDivideGeneric() =>
        "Returns the quotient — result is undefined if divisor is zero";

    internal static string PostMultiply() =>
        "Returns the product of the provided values";

    internal static string PostSubtract() =>
        "Returns the difference of the provided values";

    internal static string PostAddOrSum() =>
        "Returns the sum of the provided values";

    internal static string PostGet() =>
        "Returns the requested value — does not modify state";

    internal static string PostSetOrUpdate() =>
        "Modifies internal state — no return value or returns confirmation";

    internal static string PostAddOrInsert() =>
        "Adds an element to the collection or data store";

    internal static string PostRemoveOrDelete() =>
        "Removes the specified element if it exists";

    internal static string PostSaveOrWrite() =>
        "Persists data — may throw if write fails";

    internal static string PostLoadOrRead() =>
        "Retrieves data from storage — may throw if source is unavailable";

    internal static string PostCalculateOrCompute() =>
        "Returns a computed result based on the provided inputs";

    internal static string PostIsHasCan() =>
        "Returns a boolean indicating whether the condition holds";

    internal static string PostMayThrow() =>
        "May throw an exception under certain input conditions — see Errors section for details";

    internal static string PostStateMutation() =>
        "Modifies internal object state — call GetX() methods after this to observe changes";

    internal static string NormalizeTypeName(string returnType)
    {
        var simple = returnType.Trim();
        var angle = simple.IndexOf('<', StringComparison.Ordinal);
        if (angle > 0)
        {
            simple = simple[..angle].Trim();
        }

        return simple switch
        {
            "double" or "float" or "decimal" => simple,
            "int" or "long" or "short" or "byte" => simple,
            "bool" => "bool",
            "string" or "std::string" => "string",
            _ when simple.StartsWith("List", StringComparison.Ordinal) => "collection",
            _ when simple.Contains("IEnumerable", StringComparison.Ordinal) => "collection",
            _ => simple,
        };
    }

    internal static string TranslateOperationalLimitText(string limit, MethodAnalysisContext context)
    {
        var text = limit.Trim();
        if (text.StartsWith("When ", StringComparison.OrdinalIgnoreCase))
        {
            text = text[5..].Trim();
        }

        if (text.Contains(':', StringComparison.Ordinal))
        {
            var colon = text.IndexOf(':', StringComparison.Ordinal);
            var name = text[..colon].Trim();
            var remainder = text[(colon + 1)..].Trim();
            var translated = TranslateConditionFragment(name, remainder, context);
            if (translated is not null)
            {
                return translated;
            }
        }

        return TranslateConditionFragment(ExtractNameFromText(text), text, context) ?? HumanizeFallback(text);
    }

    private static string? TranslateConditionFragment(string name, string condition, MethodAnalysisContext context)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            name = ExtractNameFromText(condition);
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var usedAsDivisor = context.HasSourceBody && SourcePatternHelpers.IsUsedAsDivisor(context.SourceBody, name);

        if (SafeRegex.IsMatch(condition, @"\b" + Regex.Escape(name) + @"\s*==\s*0\b") ||
            condition.Contains("Must not be zero", StringComparison.OrdinalIgnoreCase) ||
            condition.Contains("used as a divisor", StringComparison.OrdinalIgnoreCase))
        {
            return GuardZero(name, usedAsDivisor);
        }

        if (SafeRegex.IsMatch(condition, @"\b" + Regex.Escape(name) + @"\s*==\s*null\b", RegexOptions.IgnoreCase) ||
            condition.Contains("nullptr", StringComparison.OrdinalIgnoreCase))
        {
            return GuardNull(name);
        }

        if (SafeRegex.IsMatch(condition, @"\b" + Regex.Escape(name) + @"\s*<=\s*0\b"))
        {
            return GuardNonPositive(name);
        }

        if (SafeRegex.IsMatch(condition, @"\b" + Regex.Escape(name) + @"\s*<\s*0\b"))
        {
            return GuardPositive(name);
        }

        if (SafeRegex.IsMatch(condition, @"\b" + Regex.Escape(name) + @"\s*==\s*""""") ||
            condition.Contains("null or empty", StringComparison.OrdinalIgnoreCase) ||
            condition.Contains("must not be empty", StringComparison.OrdinalIgnoreCase))
        {
            return GuardNullOrEmpty(name);
        }

        if (condition.Contains("index", StringComparison.OrdinalIgnoreCase) ||
            condition.Contains("bounds", StringComparison.OrdinalIgnoreCase))
        {
            return IndexBounds(name);
        }

        if (condition.Contains("division by zero", StringComparison.OrdinalIgnoreCase))
        {
            var divisor = FindDivisorParameter(context) ?? name;
            return GuardZero(divisor, usedAsDivisor: true);
        }

        return null;
    }

    private static string? FindDivisorParameter(MethodAnalysisContext context)
    {
        if (!context.HasSourceBody)
        {
            return null;
        }

        foreach (var (name, _) in SourcePatternHelpers.ParseParameters(context.Method))
        {
            if (SourcePatternHelpers.IsUsedAsDivisor(context.SourceBody, name))
            {
                return name;
            }
        }

        return null;
    }

    private static string ExtractNameFromText(string text)
    {
        var match = SafeRegex.Match(text, @"\b([A-Za-z_][\w]*)\s*(==|<=|<|!=)");
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    private static string HumanizeFallback(string text)
    {
        if (text.Contains("Potential division by zero", StringComparison.OrdinalIgnoreCase))
        {
            return "Divisor parameter must not be zero — division by zero will throw an exception";
        }

        if (text.Contains("Potential null pointer", StringComparison.OrdinalIgnoreCase))
        {
            return "Pointer parameter must not be null before dereferencing in this method";
        }

        if (text.Contains("out-of-bounds", StringComparison.OrdinalIgnoreCase))
        {
            return "Index parameter must be within valid index bounds for the accessed collection";
        }

        if (text.Contains("file path", StringComparison.OrdinalIgnoreCase))
        {
            return FilePathAccessible();
        }

        return text;
    }

    internal static List<MethodPrecondition> DeduplicatePreconditions(IReadOnlyList<MethodPrecondition> items)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<MethodPrecondition>();
        foreach (var item in items)
        {
            var key = $"{item.Subject}|{item.Description}";
            if (seen.Add(key))
            {
                results.Add(item);
            }
        }

        return results;
    }

    internal static List<MethodPostcondition> DeduplicatePostconditions(IReadOnlyList<MethodPostcondition> items)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<MethodPostcondition>();
        foreach (var item in items)
        {
            if (seen.Add(item.Description))
            {
                results.Add(item);
            }
        }

        return results;
    }
}

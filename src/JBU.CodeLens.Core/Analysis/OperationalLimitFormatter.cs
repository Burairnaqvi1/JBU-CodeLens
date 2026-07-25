using System.Text.RegularExpressions;

namespace JBU.CodeLens.Core.Analysis;

/// <summary>
/// Converts parser operational-limit strings into readable English sentences.
/// </summary>
public static class OperationalLimitFormatter
{
    public static string Format(string limit, MethodInfo method)
    {
        if (string.IsNullOrWhiteSpace(limit))
        {
            return string.Empty;
        }

        return Format(limit, new MethodAnalysisContext(method));
    }

    internal static string Format(string limit, MethodAnalysisContext context)
    {
        var text = limit.Trim();
        if (text.StartsWith("When ", StringComparison.OrdinalIgnoreCase))
        {
            text = text[5..].Trim();
        }

        var subject = string.Empty;
        var condition = text;
        if (text.Contains(':'))
        {
            var colon = text.IndexOf(':');
            subject = text[..colon].Trim();
            condition = text[(colon + 1)..].Trim();
        }

        if (string.IsNullOrEmpty(subject))
        {
            subject = ExtractSubject(condition);
        }

        var formatted = TryFormatCondition(subject, condition, context);
        if (!string.IsNullOrEmpty(formatted))
        {
            return formatted;
        }

        formatted = TryFormatCondition(subject, text, context);
        if (!string.IsNullOrEmpty(formatted))
        {
            return formatted;
        }

        return HumanizeRemaining(text, subject, context);
    }

    private static string? TryFormatCondition(string subject, string condition, MethodAnalysisContext context)
    {
        var usedAsDivisor = !string.IsNullOrEmpty(subject) &&
                            context.HasSourceBody &&
                            SourcePatternHelpers.IsUsedAsDivisor(context.SourceBody, subject);

        if (IsZeroCheck(condition) || condition.Contains("Must not be zero", StringComparison.OrdinalIgnoreCase))
        {
            if (usedAsDivisor || condition.Contains("divisor", StringComparison.OrdinalIgnoreCase))
            {
                return SubjectPrefix(subject, "must not be zero — used as a divisor in this method");
            }

            return SubjectPrefix(subject, "must not be zero — invalid values cause this method to throw");
        }

        if (IsNullCheck(condition))
        {
            return SubjectPrefix(subject, "must not be null before this method executes");
        }

        if (IsEmptyCheck(condition))
        {
            return SubjectPrefix(subject, "must not be null or empty before this method executes");
        }

        if (IsNegativeCheck(condition))
        {
            return SubjectPrefix(subject, "must be a positive value — negative values are rejected");
        }

        if (IsNonPositiveCheck(condition))
        {
            return SubjectPrefix(subject, "must be greater than zero — zero or negative values are rejected");
        }

        if (condition.Contains("index", StringComparison.OrdinalIgnoreCase) ||
            condition.Contains("bounds", StringComparison.OrdinalIgnoreCase) ||
            condition.Contains("out-of-bounds", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrEmpty(subject)
                ? "Must stay within valid array bounds"
                : $"{subject} must stay within valid array bounds";
        }

        if (condition.Contains("division by zero", StringComparison.OrdinalIgnoreCase))
        {
            var divisor = string.IsNullOrEmpty(subject) ? FindDivisorParameter(context) : subject;
            return SubjectPrefix(divisor ?? string.Empty, "must not be zero — used as a divisor in this method");
        }

        if (condition.Contains("null pointer", StringComparison.OrdinalIgnoreCase))
        {
            return SubjectPrefix(subject, "must not be null before dereferencing in this method");
        }

        if (condition.Contains("file path", StringComparison.OrdinalIgnoreCase) ||
            condition.Contains("valid file", StringComparison.OrdinalIgnoreCase))
        {
            return SubjectPrefix(subject, "must point to an accessible file path");
        }

        if (condition.Contains("memory leak", StringComparison.OrdinalIgnoreCase))
        {
            return "Allocated memory must be released to avoid a memory leak in this method";
        }

        if (condition.Contains("infinite loop", StringComparison.OrdinalIgnoreCase))
        {
            return "Loop logic must include a valid termination condition to avoid running forever";
        }

        if (condition.Contains("non-negative", StringComparison.OrdinalIgnoreCase))
        {
            return SubjectPrefix(subject, "should remain non-negative based on surrounding logic");
        }

        return null;
    }

    private static string HumanizeRemaining(string text, string subject, MethodAnalysisContext context)
    {
        if (LooksLikeCodeExpression(text))
        {
            var reformatted = TryFormatCondition(subject, text, context);
            if (!string.IsNullOrEmpty(reformatted))
            {
                return reformatted;
            }

            return SubjectPrefix(subject, $"must satisfy the required condition described by {DescribeExpression(text)}");
        }

        if (text.Contains("Potential ", StringComparison.OrdinalIgnoreCase))
        {
            return text.Replace("Potential ", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        }

        return CapitalizeSentence(text);
    }

    private static string SubjectPrefix(string subject, string sentence)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            return CapitalizeSentence(sentence);
        }

        return CapitalizeSentence($"{subject} {sentence}");
    }

    private static string CapitalizeSentence(string text)
    {
        var trimmed = text.Trim().TrimEnd('.');
        if (string.IsNullOrEmpty(trimmed))
        {
            return string.Empty;
        }

        return char.ToUpper(trimmed[0]) + trimmed[1..];
    }

    private static string ExtractSubject(string text)
    {
        var match = Regex.Match(text, @"\b([A-Za-z_][\w]*)\s*(==|<=|<|!=|>=|>)");
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    private static bool IsZeroCheck(string condition) =>
        Regex.IsMatch(condition, @"\b[A-Za-z_][\w]*\s*==\s*0\b") ||
        Regex.IsMatch(condition, @"\b0\s*==\s*[A-Za-z_][\w]*\b");

    private static bool IsNullCheck(string condition) =>
        condition.Contains("== null", StringComparison.OrdinalIgnoreCase) ||
        condition.Contains("== nullptr", StringComparison.OrdinalIgnoreCase);

    private static bool IsEmptyCheck(string condition) =>
        condition.Contains("== \"\"", StringComparison.Ordinal) ||
        condition.Contains("string.Empty", StringComparison.OrdinalIgnoreCase) ||
        condition.Contains("IsNullOrEmpty", StringComparison.OrdinalIgnoreCase) ||
        condition.Contains("IsNullOrWhiteSpace", StringComparison.OrdinalIgnoreCase) ||
        condition.Contains(".empty()", StringComparison.OrdinalIgnoreCase);

    private static bool IsNegativeCheck(string condition) =>
        Regex.IsMatch(condition, @"\b[A-Za-z_][\w]*\s*<\s*0\b");

    private static bool IsNonPositiveCheck(string condition) =>
        Regex.IsMatch(condition, @"\b[A-Za-z_][\w]*\s*<=\s*0\b");

    private static bool LooksLikeCodeExpression(string text) =>
        text.Contains("==", StringComparison.Ordinal) ||
        text.Contains("!=", StringComparison.Ordinal) ||
        text.Contains("<=", StringComparison.Ordinal) ||
        text.Contains(">=", StringComparison.Ordinal) ||
        text.Contains("->", StringComparison.Ordinal) ||
        text.Contains("&&", StringComparison.Ordinal) ||
        text.Contains("||", StringComparison.Ordinal);

    private static string DescribeExpression(string text) =>
        text.Replace("==", " equals ", StringComparison.Ordinal)
            .Replace("!=", " does not equal ", StringComparison.Ordinal)
            .Replace("<=", " is less than or equal to ", StringComparison.Ordinal)
            .Replace(">=", " is greater than or equal to ", StringComparison.Ordinal)
            .Replace("<", " is less than ", StringComparison.Ordinal)
            .Replace(">", " is greater than ", StringComparison.Ordinal);

    private static string? FindDivisorParameter(MethodAnalysisContext context)
    {
        if (!context.HasSourceBody)
        {
            return null;
        }

        foreach (var (_, name) in SourcePatternHelpers.ParseParameters(context.Method))
        {
            if (SourcePatternHelpers.IsUsedAsDivisor(context.SourceBody, name))
            {
                return name;
            }
        }

        return null;
    }
}

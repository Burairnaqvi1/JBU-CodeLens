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

        // Limits arrive as "subject: condition". A C++ expression carries its own colons in the
        // scope operator, so splitting on the first one turned `std::fabs(x) < 1e-12` into a
        // requirement about something called "std" — an entity that does not exist. Only a colon
        // that is not part of "::" separates a subject.
        var colon = IndexOfSubjectColon(text);
        if (colon > 0)
        {
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
            // A "< 0" guard permits zero, so "positive" overstates it.
            return SubjectPrefix(subject, "must not be negative — negative values are rejected");
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
        // A limit that reaches this point came from a guard of the form `if (condition) throw`, so
        // the expression describes when the method REJECTS its input, not what the caller must
        // supply. Printing it unchanged states the opposite of the truth: `!File.Exists(path)`
        // became "the file must not exist", and `iterations < 1` became a demand for fewer than one
        // iteration. Where the expression can be inverted safely the positive requirement is shown;
        // where it cannot, it is labelled as the rejection it actually is.
        var required = TryNegate(text);
        if (!string.IsNullOrEmpty(required))
        {
            return SubjectPrefix(subject, $"must satisfy the required condition described by {DescribeExpression(required)}");
        }

        if (LooksLikeCodeExpression(text) || LooksLikeBareGuard(text) || LooksLikeCallOrIndex(text))
        {
            var reformatted = TryFormatCondition(subject, text, context);
            if (!string.IsNullOrEmpty(reformatted))
            {
                return reformatted;
            }

            return CapitalizeSentence($"the method rejects input where {DescribeExpression(text)}");
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

        return char.ToUpperInvariant(trimmed[0]) + trimmed[1..];
    }

    /// <summary>
    /// Position of the colon separating "subject: condition", skipping the C++ scope operator.
    /// Returns -1 when the text carries no subject.
    /// </summary>
    private static int IndexOfSubjectColon(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != ':')
            {
                continue;
            }

            var partOfScopeOperator = (i + 1 < text.Length && text[i + 1] == ':') ||
                                      (i > 0 && text[i - 1] == ':');
            if (!partOfScopeOperator)
            {
                return i;
            }
        }

        return -1;
    }

    private static string ExtractSubject(string text)
    {
        var match = SafeRegex.Match(text, @"\b([A-Za-z_][\w]*)\s*(==|<=|<|!=|>=|>)");
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    private static bool IsZeroCheck(string condition) =>
        SafeRegex.IsMatch(condition, @"\b[A-Za-z_][\w]*\s*==\s*0\b") ||
        SafeRegex.IsMatch(condition, @"\b0\s*==\s*[A-Za-z_][\w]*\b");

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
        SafeRegex.IsMatch(condition, @"\b[A-Za-z_][\w]*\s*<\s*0\b");

    private static bool IsNonPositiveCheck(string condition) =>
        SafeRegex.IsMatch(condition, @"\b[A-Za-z_][\w]*\s*<=\s*0\b");

    /// <summary>
    /// Inverts a guard condition into the requirement it implies, or returns null when the
    /// expression cannot be inverted without changing its meaning.
    /// </summary>
    /// <remarks>
    /// Only single-operator expressions and a leading logical NOT are handled. A compound joined by
    /// &amp;&amp; or || inverts to a form whose English reading is no longer obvious, so those are
    /// reported as rejections rather than guessed at — a limit stated backwards is worse than one
    /// stated cautiously.
    /// </remarks>
    private static string? TryNegate(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0 ||
            trimmed.Contains("&&", StringComparison.Ordinal) ||
            trimmed.Contains("||", StringComparison.Ordinal))
        {
            return null;
        }

        if (trimmed[0] == '!' && !trimmed.StartsWith("!=", StringComparison.Ordinal))
        {
            var inner = trimmed[1..].Trim();
            return inner.Length > 0 ? inner : null;
        }

        // Two-character operators are tested first: finding '<' inside "<=" would invert it wrongly.
        (string Operator, string Inverse)[] operators =
        [
            ("==", "!="), ("!=", "=="), ("<=", ">"), (">=", "<"), ("<", ">="), (">", "<="),
        ];

        foreach (var (op, inverse) in operators)
        {
            var index = trimmed.IndexOf(op, StringComparison.Ordinal);
            if (index <= 0 || index + op.Length >= trimmed.Length)
            {
                continue;
            }

            // A second comparison means more than one relation to invert; leave it alone.
            var rest = trimmed[(index + op.Length)..];
            if (rest.Contains('<', StringComparison.Ordinal) || rest.Contains('>', StringComparison.Ordinal) ||
                rest.Contains("==", StringComparison.Ordinal) || rest.Contains("!=", StringComparison.Ordinal))
            {
                return null;
            }

            return string.Concat(trimmed.AsSpan(0, index), inverse, rest);
        }

        return null;
    }

    /// <summary>
    /// A guard carrying no comparison operator: a bare flag as in <c>if (inQuotes) throw</c>, or a
    /// predicate call as in <c>if (string.Equals(from, to)) throw</c>. Both still describe when the
    /// method rejects its input, so both must be labelled rather than printed as requirements.
    /// </summary>
    private static bool LooksLikeBareGuard(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0 || !(char.IsLetter(trimmed[0]) || trimmed[0] == '_'))
        {
            return false;
        }

        return !trimmed.Contains(' ', StringComparison.Ordinal) ||
               SafeRegex.IsMatch(trimmed, @"^[A-Za-z_][\w.]*\s*\(");
    }

    private static bool LooksLikeCodeExpression(string text) =>
        text.Contains("==", StringComparison.Ordinal) ||
        text.Contains("!=", StringComparison.Ordinal) ||
        text.Contains("<=", StringComparison.Ordinal) ||
        text.Contains(">=", StringComparison.Ordinal) ||
        text.Contains("->", StringComparison.Ordinal) ||
        text.Contains("&&", StringComparison.Ordinal) ||
        text.Contains("||", StringComparison.Ordinal);

    /// <summary>
    /// An expression that calls or indexes something, and so is code rather than prose.
    /// </summary>
    private static bool LooksLikeCallOrIndex(string text) =>
        text.Contains('(', StringComparison.Ordinal) || text.Contains('[', StringComparison.Ordinal);

    /// <summary>
    /// Renders comparison operators as words.
    /// </summary>
    /// <remarks>
    /// Only spaced operators are rewritten. C++ template arguments carry unspaced angle brackets, so
    /// rewriting every one of them turned <c>static_cast&lt;size_t&gt;(size) &gt; matrix.size()</c>
    /// into "static_cast is less than size_t is greater than (size)…". Conditions come from
    /// whitespace-normalised source, where a real comparison always has spaces around it.
    /// </remarks>
    private static string DescribeExpression(string text) =>
        text.Replace(" == ", " equals ", StringComparison.Ordinal)
            .Replace(" != ", " does not equal ", StringComparison.Ordinal)
            .Replace(" <= ", " is less than or equal to ", StringComparison.Ordinal)
            .Replace(" >= ", " is greater than or equal to ", StringComparison.Ordinal)
            .Replace(" < ", " is less than ", StringComparison.Ordinal)
            .Replace(" > ", " is greater than ", StringComparison.Ordinal);

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

using System.Text.RegularExpressions;

namespace JBU.CodeLens.Core.Analysis;

/// <summary>
/// A single deterministic inference rule.
/// </summary>
public sealed class AnalysisRule<TFinding>
{
    public string Id { get; }
    public string Description { get; }
    private readonly Func<MethodAnalysisContext, IEnumerable<TFinding>> _evaluate;

    public AnalysisRule(
        string id,
        string description,
        Func<MethodAnalysisContext, IEnumerable<TFinding>> evaluate)
    {
        Id = id;
        Description = description;
        _evaluate = evaluate;
    }

    public IEnumerable<TFinding> Evaluate(MethodAnalysisContext context) => _evaluate(context);
}

/// <summary>
/// Evaluates registered rules without modifying analyzer code when new rules are added.
/// </summary>
public sealed class RuleEngine<TFinding>
{
    private readonly List<AnalysisRule<TFinding>> _rules = new();

    public RuleEngine<TFinding> Register(AnalysisRule<TFinding> rule)
    {
        _rules.Add(rule);
        return this;
    }

    public RuleEngine<TFinding> Register(
        string id,
        string description,
        Func<MethodAnalysisContext, IEnumerable<TFinding>> evaluate) =>
        Register(new AnalysisRule<TFinding>(id, description, evaluate));

    public IReadOnlyList<TFinding> EvaluateAll(MethodAnalysisContext context)
    {
        var results = new List<TFinding>();
        foreach (var rule in _rules)
        {
            results.AddRange(rule.Evaluate(context));
        }

        return results;
    }

    public IReadOnlyList<AnalysisRule<TFinding>> Rules => _rules;
}

/// <summary>
/// Shared text helpers for source-based rules.
/// </summary>
internal static class SourcePatternHelpers
{
    internal static IEnumerable<string> ExtractParameterNames(MethodInfo method)
    {
        foreach (var parameter in method.Parameters)
        {
            var name = ParseParameterName(parameter);
            if (!string.IsNullOrEmpty(name))
            {
                yield return name;
            }
        }
    }

    internal static string? ParseParameterName(string parameter)
    {
        return ParseParameter(parameter).Name;
    }

    internal static (string Type, string Name) ParseParameter(string parameter)
    {
        var trimmed = parameter.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return (string.Empty, string.Empty);
        }

        var parts = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return (trimmed, string.Empty);
        }

        var name = parts[^1].Trim(',', '&', '*', '&');
        var type = string.Join(" ", parts[..^1]);
        return (type, name);
    }

    internal static IEnumerable<(string Type, string Name)> ParseParameters(MethodInfo method)
    {
        foreach (var parameter in method.Parameters)
        {
            var parsed = ParseParameter(parameter);
            if (!string.IsNullOrEmpty(parsed.Name))
            {
                yield return parsed;
            }
        }
    }

    internal static bool IsUsedAsDivisor(string source, string identifier)
    {
        if (string.IsNullOrEmpty(identifier) || !source.Contains('/', StringComparison.Ordinal))
        {
            return false;
        }

        var escaped = Regex.Escape(identifier);
        return SafeRegex.IsMatch(source, $@"/\s*{escaped}\b|/\s*\(\s*{escaped}\s*\)|/\s*\(\s*[^)]*\b{escaped}\b") ||
               SafeRegex.IsMatch(source, $@"%\s*{escaped}\b");
    }

    internal static bool HasNullGuard(string source, string identifier)
    {
        if (string.IsNullOrEmpty(identifier))
        {
            return false;
        }

        var escaped = Regex.Escape(identifier);
        return SafeRegex.IsMatch(source, $@"\b{escaped}\s*==\s*null\b", RegexOptions.IgnoreCase) ||
               SafeRegex.IsMatch(source, $@"\b{escaped}\s*!=\s*null\b", RegexOptions.IgnoreCase) ||
               SafeRegex.IsMatch(source, $@"\b{escaped}\s*==\s*nullptr\b", RegexOptions.IgnoreCase) ||
               SafeRegex.IsMatch(source, $@"\b{escaped}\s*!=\s*nullptr\b", RegexOptions.IgnoreCase) ||
               source.Contains($"ThrowIfNull({identifier}", StringComparison.Ordinal) ||
               source.Contains($"ArgumentNullException.ThrowIfNull({identifier}", StringComparison.Ordinal);
    }

    internal static bool IsUsedAsIndex(string source, string identifier)
    {
        if (string.IsNullOrEmpty(identifier))
        {
            return false;
        }

        return source.Contains($"{identifier}[", StringComparison.Ordinal) ||
               SafeRegex.IsMatch(source, $@"\[\s*{Regex.Escape(identifier)}\s*\]");
    }

    internal static bool MethodIteratesParameter(string source, string identifier)
    {
        if (string.IsNullOrEmpty(identifier))
        {
            return false;
        }

        var escaped = Regex.Escape(identifier);
        return SafeRegex.IsMatch(source, $@"\bforeach\s*\([^)]*\b{escaped}\b") ||
               SafeRegex.IsMatch(source, $@"\bfor\s*\([^)]*\b{escaped}\b") ||
               source.Contains($"foreach (var item in {identifier}", StringComparison.Ordinal) ||
               source.Contains($"foreach (auto", StringComparison.Ordinal) && source.Contains(identifier, StringComparison.Ordinal);
    }

    internal static bool IsNumericType(string type)
    {
        var simple = type.Trim().TrimEnd('&', '*');
        return simple is "double" or "float" or "decimal" or "int" or "long" or "short" or "byte" or "size_t" ||
               simple.Contains("double", StringComparison.Ordinal) ||
               simple.Contains("float", StringComparison.Ordinal) ||
               simple.Contains("int", StringComparison.Ordinal);
    }

    internal static bool IsFloatingType(string type)
    {
        var simple = type.Trim().TrimEnd('&', '*');
        return simple is "double" or "float" or "decimal" ||
               simple.Contains("double", StringComparison.Ordinal) ||
               simple.Contains("float", StringComparison.Ordinal);
    }

    internal static bool IsStringType(string type)
    {
        var simple = type.Trim().TrimEnd('&', '*');
        return simple is "string" or "std::string" or "String" ||
               simple.Contains("string", StringComparison.OrdinalIgnoreCase) ||
               simple.Contains("char*", StringComparison.Ordinal);
    }

    internal static bool IsCollectionType(string type)
    {
        var simple = type.Trim();
        return simple.Contains("List", StringComparison.Ordinal) ||
               simple.Contains("IEnumerable", StringComparison.Ordinal) ||
               simple.Contains("Collection", StringComparison.Ordinal) ||
               simple.Contains("vector", StringComparison.Ordinal) ||
               simple.Contains("array", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool HasFileIoOperations(string source) =>
        source.Contains("File.Read", StringComparison.Ordinal) ||
        source.Contains("File.Write", StringComparison.Ordinal) ||
        source.Contains("File.Open", StringComparison.Ordinal) ||
        source.Contains("StreamReader", StringComparison.Ordinal) ||
        source.Contains("StreamWriter", StringComparison.Ordinal) ||
        source.Contains("ifstream", StringComparison.Ordinal) ||
        source.Contains("ofstream", StringComparison.Ordinal) ||
        source.Contains("fopen(", StringComparison.Ordinal);

    internal static bool HasSqrtUsage(string source, string identifier)
    {
        if (string.IsNullOrEmpty(identifier))
        {
            return false;
        }

        return SafeRegex.IsMatch(source, $@"\bMath\.Sqrt\s*\(\s*{Regex.Escape(identifier)}\s*\)", RegexOptions.IgnoreCase) ||
               SafeRegex.IsMatch(source, $@"\bsqrt\s*\(\s*{Regex.Escape(identifier)}\s*\)", RegexOptions.IgnoreCase) ||
               SafeRegex.IsMatch(source, $@"\bstd::sqrt\s*\(\s*{Regex.Escape(identifier)}\s*\)", RegexOptions.IgnoreCase);
    }

    internal static bool ContainsThrow(string source) =>
        SafeRegex.IsMatch(source, @"\bthrow\b");

    internal static bool GuardFollowedByThrow(string source, int guardEnd)
    {
        if (guardEnd >= source.Length)
        {
            return false;
        }

        var afterGuard = source[guardEnd..];
        var braceIndex = afterGuard.IndexOf('{', StringComparison.Ordinal);
        if (braceIndex < 0)
        {
            return SafeRegex.IsMatch(afterGuard[..Math.Min(afterGuard.Length, 120)], @"\bthrow\b");
        }

        var block = ExtractBalancedBlock(afterGuard, braceIndex);
        return block.Contains("throw", StringComparison.Ordinal);
    }

    internal static bool IsWrittenInSource(string source, string identifier)
    {
        if (string.IsNullOrEmpty(identifier))
        {
            return false;
        }

        var escaped = Regex.Escape(identifier);
        return SafeRegex.IsMatch(source, $@"\b{escaped}\s*=(?!=)", RegexOptions.None) ||
               SafeRegex.IsMatch(source, $@"\b{escaped}\s*(\+\+|--)", RegexOptions.None) ||
               SafeRegex.IsMatch(source, $@"(\+\+|--)\s*{escaped}\b", RegexOptions.None) ||
               SafeRegex.IsMatch(source, $@"\b{escaped}\s*\+=", RegexOptions.None) ||
               SafeRegex.IsMatch(source, $@"\b{escaped}\s*-=", RegexOptions.None);
    }

    internal static bool IsReadInSource(string source, string identifier)
    {
        if (string.IsNullOrEmpty(identifier) || !source.Contains(identifier, StringComparison.Ordinal))
        {
            return false;
        }

        return IsWrittenInSource(source, identifier) ||
               SafeRegex.IsMatch(source, $@"\b{Regex.Escape(identifier)}\b");
    }

    internal static VariableUsageKind ClassifyUsage(string source, string identifier, bool isDeclared)
    {
        if (!isDeclared && !source.Contains(identifier, StringComparison.Ordinal))
        {
            return VariableUsageKind.Unused;
        }

        var read = IsReadInSource(source, identifier);
        var written = IsWrittenInSource(source, identifier);

        if (written && read)
        {
            return VariableUsageKind.ReadWrite;
        }

        if (written)
        {
            return VariableUsageKind.Written;
        }

        if (read)
        {
            return VariableUsageKind.Read;
        }

        return VariableUsageKind.Unused;
    }

    internal static bool HasCatchWithoutRethrow(string source)
    {
        foreach (Match match in SafeRegex.Matches(source, @"catch\s*(?:\([^)]*\))?\s*\{", RegexOptions.IgnoreCase))
        {
            var openBrace = match.Index + match.Length - 1;
            var block = ExtractBalancedBlock(source, openBrace);
            if (string.IsNullOrEmpty(block))
            {
                continue;
            }

            if (!SafeRegex.IsMatch(block, @"\bthrow\b"))
            {
                return true;
            }
        }

        return false;
    }

    internal static string ExtractBalancedBlock(string source, int openBraceIndex)
    {
        if (openBraceIndex < 0 || openBraceIndex >= source.Length || source[openBraceIndex] != '{')
        {
            return string.Empty;
        }

        var depth = 0;
        for (var i = openBraceIndex; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source[openBraceIndex..(i + 1)];
                }
            }
        }

        return source[openBraceIndex..];
    }

    internal static IEnumerable<Match> MatchGuardThrows(string source, string pattern)
    {
        foreach (Match match in SafeRegex.Matches(source, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            if (GuardFollowedByThrow(source, match.Index + match.Length))
            {
                yield return match;
            }
        }
    }
}

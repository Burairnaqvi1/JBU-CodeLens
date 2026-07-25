using System.Text;

namespace JBU.CodeLens.Core.Analysis;

/// <summary>
/// Produces a deterministic, plain-English one-line description of a method from its name,
/// parameters, and return type. Used as an always-available organic fallback for the
/// "Brief Description" heading when no XML documentation and no AI output is available.
/// </summary>
public static class MethodDescriptionBuilder
{
    private static readonly HashSet<string> BooleanPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Is", "Has", "Can", "Should", "Was", "Are", "Contains", "Exists", "Supports",
    };

    /// <summary>
    /// Builds a single readable sentence describing what the method most likely does.
    /// Never throws and always returns a non-empty string.
    /// </summary>
    public static string Build(MethodInfo method)
    {
        var name = method.Name ?? string.Empty;
        var parentName = method.ParentClass?.Name;

        // Constructor
        if (!string.IsNullOrEmpty(parentName) && name.Equals(parentName, StringComparison.Ordinal))
        {
            return method.Parameters.Count > 0
                ? $"Constructs a new {parentName} instance using {JoinNames(ParameterNames(method))}."
                : $"Constructs a new {parentName} instance.";
        }

        var words = SplitIdentifier(name);
        if (words.Count == 0)
        {
            return $"Represents the {name} member.";
        }

        var verb = words[0];
        var remainder = string.Join(" ", words.Skip(1)).Trim().ToLowerInvariant();
        var paramNames = ParameterNames(method);
        var returnType = method.ReturnType ?? "void";
        var isBoolReturn = IsBooleanType(returnType);

        var sb = new StringBuilder();

        // Boolean/query style: "Is", "Has", "Can", or bool return type.
        if (BooleanPrefixes.Contains(verb) || (isBoolReturn && !IsActionVerb(verb)))
        {
            var subjectIsParams = string.IsNullOrEmpty(remainder) && paramNames.Count > 0;
            var subject = string.IsNullOrEmpty(remainder)
                ? (paramNames.Count > 0 ? JoinNames(paramNames) : "the current state")
                : remainder;
            sb.Append($"Determines whether {subject}");
            if (BooleanPrefixes.Contains(verb) && !string.IsNullOrEmpty(remainder))
            {
                sb.Clear();
                sb.Append($"Determines whether the {remainder} condition holds");
            }

            // When the parameters already serve as the subject, a "based on" clause would just
            // repeat them ("Determines whether input, based on input").
            if (paramNames.Count > 0 && !subjectIsParams)
            {
                sb.Append($", based on {JoinNames(paramNames)}");
            }

            sb.Append('.');
            return Capitalize(sb.ToString());
        }

        // Action style: "CalculateTotal", "SaveFile", etc.
        sb.Append(Conjugate(verb));
        if (!string.IsNullOrEmpty(remainder))
        {
            sb.Append(' ').Append(remainder);
        }

        if (paramNames.Count > 0)
        {
            // A bare verb reads best with the parameter as its object ("Applies the given
            // theme"); a verb with an object keeps the parameters as instruments
            // ("Saves file using path") — "from" wrongly implied extraction.
            sb.Append(remainder.Length == 0 ? " the given " : " using ");
            sb.Append(JoinNames(paramNames));
        }

        if (!IsVoidType(returnType))
        {
            sb.Append(", returning ").Append(DescribeReturn(returnType));
        }

        sb.Append('.');
        return Capitalize(sb.ToString());
    }

    private static List<string> ParameterNames(MethodInfo method)
    {
        var names = new List<string>();
        foreach (var raw in method.Parameters)
        {
            var parts = raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                continue;
            }

            names.Add(parts[^1].TrimStart('&', '*'));
        }

        return names;
    }

    private static string JoinNames(IReadOnlyList<string> names)
    {
        if (names.Count == 0) return string.Empty;
        if (names.Count == 1) return names[0];
        if (names.Count == 2) return $"{names[0]} and {names[1]}";
        return $"{string.Join(", ", names.Take(names.Count - 1))}, and {names[^1]}";
    }

    private static List<string> SplitIdentifier(string identifier)
    {
        var words = new List<string>();
        if (string.IsNullOrEmpty(identifier))
        {
            return words;
        }

        var current = new StringBuilder();
        foreach (var ch in identifier)
        {
            if (ch is '_' or '-')
            {
                if (current.Length > 0)
                {
                    words.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            if (char.IsUpper(ch) && current.Length > 0 && !char.IsUpper(current[^1]))
            {
                words.Add(current.ToString());
                current.Clear();
            }

            current.Append(ch);
        }

        if (current.Length > 0)
        {
            words.Add(current.ToString());
        }

        return words;
    }

    private static string Conjugate(string verb)
    {
        if (string.IsNullOrEmpty(verb))
        {
            return "Processes";
        }

        var lower = verb.ToLowerInvariant();
        string conjugated;

        if (lower.EndsWith("y") && lower.Length > 1 && !"aeiou".Contains(lower[^2]))
        {
            conjugated = lower[..^1] + "ies";
        }
        else if (lower.EndsWith("s") || lower.EndsWith("sh") || lower.EndsWith("ch") ||
                 lower.EndsWith("x") || lower.EndsWith("z") || lower.EndsWith("o"))
        {
            conjugated = lower + "es";
        }
        else
        {
            conjugated = lower + "s";
        }

        return Capitalize(conjugated);
    }

    private static bool IsActionVerb(string verb) =>
        verb is "Get" or "Set" or "Create" or "Build" or "Make" or "Load" or "Save" or
                "Compute" or "Calculate" or "Find" or "Fetch" or "Read" or "Write";

    private static string DescribeReturn(string returnType)
    {
        var simple = NormalizeType(returnType);

        if (simple.Contains("Task", StringComparison.OrdinalIgnoreCase))
        {
            return "a task that completes asynchronously";
        }

        if (IsBooleanType(simple))
        {
            return "true when the condition holds, otherwise false";
        }

        if (IsCollectionType(simple))
        {
            return "a collection of results";
        }

        return simple switch
        {
            "string" or "String" => "the resulting text",
            "int" or "long" or "short" or "byte" or "double" or "float" or "decimal" =>
                $"the computed {simple} value",
            _ => $"the resulting {simple}",
        };
    }

    private static string NormalizeType(string type)
    {
        var simple = type.Trim().TrimEnd('&', '*', '?');
        var angle = simple.IndexOf('<');
        if (angle > 0)
        {
            simple = simple[..angle].Trim();
        }

        var scope = simple.LastIndexOf("::", StringComparison.Ordinal);
        if (scope >= 0)
        {
            simple = simple[(scope + 2)..];
        }

        var dot = simple.LastIndexOf('.');
        if (dot >= 0)
        {
            simple = simple[(dot + 1)..];
        }

        return simple;
    }

    private static bool IsVoidType(string type)
    {
        var simple = NormalizeType(type);
        if (simple.Equals("void", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Task<T> produces a value; only the non-generic Task is void-like.
        return simple.Equals("Task", StringComparison.OrdinalIgnoreCase) &&
               !type.Contains('<');
    }

    private static bool IsBooleanType(string type)
    {
        var simple = NormalizeType(type);
        return simple is "bool" or "Boolean";
    }

    private static bool IsCollectionType(string type)
    {
        var simple = NormalizeType(type);
        return type.Contains("[]", StringComparison.Ordinal) ||
               simple is "List" or "IEnumerable" or "ICollection" or "IList" or "Array" or
                         "Dictionary" or "IReadOnlyList" or "HashSet" or "vector" or "Collection";
    }

    private static string Capitalize(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        return char.ToUpperInvariant(text[0]) + text[1..];
    }
}

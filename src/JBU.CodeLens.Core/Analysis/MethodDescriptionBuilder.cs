using System.Diagnostics.CodeAnalysis;
using System.Globalization;
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

    /// <summary>Words that already govern the argument that follows them in a method name.</summary>
    private static readonly HashSet<string> Prepositions = new(StringComparer.OrdinalIgnoreCase)
    {
        "Of", "For", "From", "To", "With", "By", "In", "On", "At", "Into", "Between", "Against",
    };

    /// <summary>
    /// Well-known algorithm names that read as compound nouns rather than as a verb and its object.
    /// </summary>
    /// <remarks>
    /// Held as a list because the distinction cannot be derived: "MergeSort" and "ComputeHash" have
    /// the same shape, a verb followed by a word that is also a verb, yet only the first is the
    /// name of a thing. Anything absent from this list is treated as a verb phrase, which is the
    /// right default for the overwhelming majority of method names.
    /// </remarks>
    private static readonly HashSet<string> NamedOperations = new(StringComparer.OrdinalIgnoreCase)
    {
        "merge sort", "quick sort", "heap sort", "bubble sort", "insertion sort", "selection sort",
        "radix sort", "shell sort", "counting sort", "bucket sort", "topological sort",
        "binary search", "linear search", "depth first search", "breadth first search",
    };

    /// <summary>
    /// Leading words that are safe to conjugate as verbs.
    /// </summary>
    /// <remarks>
    /// Not every method name begins with a verb. <c>LevenshteinDistance</c>, <c>ShortestPath</c> and
    /// <c>WordFrequency</c> name the thing produced rather than the act of producing it, and
    /// conjugating their first word yields "Levenshteins distance", "Shortests path" and "Words
    /// frequency", text that reads as broken English and undermines confidence in every other
    /// heading on the page. A name whose first word is not in this set is described as the noun
    /// phrase it actually is.
    /// </remarks>
    private static readonly HashSet<string> KnownVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "Accumulate", "Acquire", "Add", "Aggregate", "Allocate", "Append", "Apply", "Approximate",
        "Assert", "Assign", "Attach", "Begin", "Bind", "Buffer", "Build", "Consume", "Decompose",
        "Dispatch", "Drain", "Evict", "Flatten", "Fold", "Interleave", "Order", "Partition",
        "Precede", "Rebalance", "Rotate", "Slice", "Slide", "Stream", "Summarise", "Summarize",
        "Tokenize", "Tokenise", "Touch", "Try", "Zip",
        "Calculate", "Cancel", "Cast", "Check", "Clamp", "Clear", "Clone", "Close", "Collapse",
        "Construct",
        "Collect", "Combine", "Commit", "Compare", "Compile", "Compose", "Compress", "Compute",
        "Concatenate", "Configure", "Connect", "Convert", "Copy", "Count", "Create", "Decode",
        "Decompress", "Decrement", "Decrypt", "Delete", "Dequeue", "Derive", "Describe",
        "Deserialize", "Detach", "Determine", "Disable", "Disconnect", "Dispatch", "Displace",
        "Dispose", "Divide", "Draw", "Emit", "Enable", "Encode", "Encrypt", "End", "Enqueue",
        "Ensure", "Enumerate", "Escape", "Estimate", "Evaluate", "Execute", "Expand", "Export",
        "Extract", "Fetch", "Fill", "Filter", "Finalize", "Find", "Flush", "Format", "Generate",
        "Get", "Group", "Grow", "Handle", "Hash", "Hide", "Import", "Increment", "Infer",
        "Initialize", "Inject", "Insert", "Inspect", "Install", "Integrate", "Interpolate",
        "Invert", "Invoke", "Iterate", "Join", "Load", "Locate", "Lock", "Log", "Lookup", "Make",
        "Map", "Match", "Maximize", "Merge", "Migrate", "Minimize", "Move", "Multiply", "Negate",
        "Normalize", "Notify", "Open", "Optimize", "Pack", "Pad", "Parse", "Peek", "Poll", "Pop",
        "Prepare", "Print", "Process", "Produce", "Project", "Prune", "Publish", "Purge", "Push",
        "Query", "Rank", "Read", "Rebuild", "Recalculate", "Receive", "Recompute", "Reduce",
        "Reformat", "Refresh", "Register", "Release", "Reload", "Remove", "Rename", "Render",
        "Replace", "Report", "Reserve", "Reset", "Resize", "Resolve", "Restore", "Retry",
        "Reverse", "Rollback", "Rotate", "Round", "Run", "Sample", "Sanitize", "Save", "Scale",
        "Scan", "Schedule", "Score", "Search", "Seek", "Select", "Send", "Serialize", "Set",
        "Shift", "Show", "Shrink", "Shuffle", "Sign", "Skip", "Solve", "Sort", "Split", "Start",
        "Stop", "Store", "Strip", "Submit", "Subscribe", "Subtract", "Sum", "Swap", "Sync",
        "Synchronize", "Take", "Terminate", "Toggle", "Trace", "Transform", "Translate",
        "Transpose", "Traverse", "Trim", "Truncate", "Unbind", "Unlock", "Unpack", "Unregister",
        "Update", "Validate", "Verify", "Visit", "Wait", "Walk", "Wrap", "Write",
    };

    /// <summary>
    /// Builds a single readable sentence describing what the method most likely does.
    /// Never throws and always returns a non-empty string.
    /// </summary>
    [SuppressMessage(
        "Globalization",
        "CA1308:Normalize strings to uppercase",
        Justification = "The lowercased text is rendered into an English prose description shown to " +
                        "the user, not used for comparison or as a security decision. CA1308 exists " +
                        "to prevent lossy round-trips in case-normalisation for lookups; upper-casing " +
                        "here would produce visibly wrong output.")]
    public static string Build(MethodInfo method)
    {
        ArgumentNullException.ThrowIfNull(method);

        // A templated type carries its parameter list in the cursor spelling, so a constructor
        // arrived as "MemoizingCache<Key, Value>": it no longer matched its own class name, so the
        // constructor was never recognised, and the argument list was split into words and read
        // back as prose, "the memoizing cache< key, value>".
        var name = StripTemplateArguments(method.Name ?? string.Empty);
        var parentName = StripTemplateArguments(method.ParentClass?.Name ?? string.Empty);
        if (parentName.Length == 0)
        {
            parentName = null;
        }

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

        // "TryParse", "try_submit": the leading word is a verb, but conjugating it produces "Tries
        // submit". The idiom means an attempt that may fail, and reads correctly as one.
        if (verb.Equals("Try", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(remainder))
        {
            sb.Append("Attempts to ").Append(remainder);
            if (paramNames.Count > 0)
            {
                sb.Append(" using ").Append(JoinNames(paramNames));
            }

            if (!IsVoidType(returnType))
            {
                sb.Append(", returning ").Append(DescribeReturn(returnType));
            }

            sb.Append('.');
            return Capitalize(sb.ToString());
        }

        // Boolean/query style: "Is", "Has", "Can", or bool return type.
        // Tested against the full verb set rather than the short action list: a bool-returning
        // method named with a plain verb ("Consume", "Matches") was forced down this path and
        // described as "Determines whether text, position, and keyword", a sentence about its
        // parameters that says nothing about the method.
        if (BooleanPrefixes.Contains(verb) || (isBoolReturn && !IsVerbLike(verb)))
        {
            var subjectIsParams = string.IsNullOrEmpty(remainder) && paramNames.Count > 0;
            string subject;
            if (!string.IsNullOrEmpty(remainder))
            {
                subject = remainder;
            }
            else
            {
                subject = paramNames.Count > 0 ? JoinNames(paramNames) : "the current state";
            }
            sb.Append(CultureInfo.InvariantCulture, $"Determines whether {subject}");
            if (BooleanPrefixes.Contains(verb) && !string.IsNullOrEmpty(remainder))
            {
                sb.Clear();
                sb.Append(CultureInfo.InvariantCulture, $"Determines whether the {remainder} condition holds");
            }

            // When the parameters already serve as the subject, a "based on" clause would just
            // repeat them ("Determines whether input, based on input").
            if (paramNames.Count > 0 && !subjectIsParams)
            {
                sb.Append(CultureInfo.InvariantCulture, $", based on {JoinNames(paramNames)}");
            }

            sb.Append('.');
            return Capitalize(sb.ToString());
        }

        // Named operations: "MergeSort", "QuickSort", "BinarySearch". Their first word is a genuine
        // verb, so conjugating it is grammatical but wrong in substance, "Merges sort" reads as an
        // instruction to merge a sort. These are compound nouns naming an algorithm, and nothing in
        // the identifier itself distinguishes them from a verb and its object ("ComputeHash"), so
        // they are recognised by name.
        var joined = string.Join(" ", words).ToLowerInvariant();
        if (NamedOperations.Contains(joined))
        {
            sb.Append("Performs the ").Append(joined);
            if (paramNames.Count > 0)
            {
                sb.Append(" of ").Append(JoinNames(paramNames));
            }

            if (!IsVoidType(returnType))
            {
                sb.Append(", returning ").Append(DescribeReturn(returnType));
            }

            sb.Append('.');
            return Capitalize(sb.ToString());
        }

        // Noun-phrase style: "LevenshteinDistance", "ShortestPath", "WordFrequency". The name
        // states what is produced, not an action, so it is described rather than conjugated.
        if (!IsVerbLike(verb))
        {
            var phrase = string.Join(" ", words).ToLowerInvariant();
            sb.Append(IsVoidType(returnType) ? "Handles the " : "Computes the ").Append(phrase);

            // A name ending in a preposition already governs its argument: "HeightOf" reads as
            // "the height of node", not "the height of from node".
            var endsWithPreposition = Prepositions.Contains(words[^1]);
            if (paramNames.Count > 0)
            {
                sb.Append(endsWithPreposition ? " " : " from ").Append(JoinNames(paramNames));
            }

            if (!IsVoidType(returnType))
            {
                sb.Append(", returning ").Append(DescribeReturn(returnType));
            }

            sb.Append('.');
            return Capitalize(sb.ToString());
        }

        // Action style: "CalculateTotal", "SaveFile", etc.
        sb.Append(Conjugate(verb));
        if (!string.IsNullOrEmpty(remainder))
        {
            sb.Append(' ').Append(ConjugateCoordinatedVerbs(words.Skip(1)));
        }

        if (paramNames.Count > 0)
        {
            // A bare verb reads best with the parameter as its object ("Applies the given
            // theme"); a verb with an object keeps the parameters as instruments
            // ("Saves file using path"), "from" wrongly implied extraction.
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

    private static string JoinNames(List<string> names)
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

    [SuppressMessage(
        "Globalization",
        "CA1308:Normalize strings to uppercase",
        Justification = "Same as Build: the lowercased verb is conjugated into English prose for " +
                        "display, never compared or used for a security decision.")]
    private static string Conjugate(string verb)
    {
        if (string.IsNullOrEmpty(verb))
        {
            return "Processes";
        }

        return Capitalize(Inflect(verb.ToLowerInvariant()));
    }

    /// <summary>
    /// Renders the words following the leading verb, inflecting a second verb introduced by "and".
    /// </summary>
    /// <remarks>
    /// "ReadAndFilterLines" produced "Reads and filter lines", because only the first word was ever
    /// conjugated. English requires both verbs of a coordinated pair to agree with the subject.
    /// </remarks>
    [SuppressMessage(
        "Globalization",
        "CA1308:Normalize strings to uppercase",
        Justification = "Same as Build: lowercased for English prose shown to the user, never " +
                        "compared or used for a security decision.")]
    private static string ConjugateCoordinatedVerbs(IEnumerable<string> words)
    {
        var parts = words.ToList();
        var sb = new StringBuilder();

        // A coordinator means the words around it form a verb chain, and every verb in the chain
        // must agree with the subject: "GroupRankAndSlice" gave "Groups rank and slice", inflecting
        // only the first. Without a coordinator the remaining words are the object of a single verb
        // ("Adds to range") and must be left alone.
        var coordinated = parts.Any(part =>
            part.Equals("and", StringComparison.OrdinalIgnoreCase) ||
            part.Equals("or", StringComparison.OrdinalIgnoreCase));

        for (var i = 0; i < parts.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(' ');
            }

            var lower = parts[i].ToLowerInvariant();
            sb.Append(coordinated && IsVerbLike(parts[i]) ? Inflect(lower) : lower);
        }

        return sb.ToString();
    }

    private static string Inflect(string lower)
    {
        string conjugated;

        // Already third-person singular ("matches", "consumes"): inflecting again gives "matcheses".
        if ((lower.EndsWith("es", StringComparison.Ordinal) && lower.Length > 2 && KnownVerbs.Contains(lower[..^2])) ||
            (lower.EndsWith('s') && lower.Length > 1 && KnownVerbs.Contains(lower[..^1])))
        {
            return lower;
        }

        if (lower.EndsWith('y') && lower.Length > 1 && !"aeiou".Contains(lower[^2], StringComparison.Ordinal))
        {
            conjugated = lower[..^1] + "ies";
        }
        else if (lower.EndsWith('s') || lower.EndsWith("sh", StringComparison.Ordinal) ||
                 lower.EndsWith("ch", StringComparison.Ordinal) ||
                 lower.EndsWith('x') || lower.EndsWith('z') || lower.EndsWith('o'))
        {
            conjugated = lower + "es";
        }
        else
        {
            conjugated = lower + "s";
        }

        return conjugated;
    }

    /// <summary>Removes a template argument list, leaving the bare identifier.</summary>
    private static string StripTemplateArguments(string identifier)
    {
        var angle = identifier.IndexOf('<', StringComparison.Ordinal);
        return angle > 0 ? identifier[..angle].Trim() : identifier.Trim();
    }

    /// <summary>
    /// Whether a word acts as a verb, including a form that is already third-person singular.
    /// </summary>
    /// <remarks>
    /// A method may be named with the inflected form directly, <c>Matches</c>, <c>Consumes</c>, 
    /// and conjugating those again yields "Matcheses". Recognising the stem keeps them intact.
    /// </remarks>
    private static bool IsVerbLike(string word)
    {
        if (KnownVerbs.Contains(word))
        {
            return true;
        }

        if (word.EndsWith("es", StringComparison.OrdinalIgnoreCase) && word.Length > 2 &&
            KnownVerbs.Contains(word[..^2]))
        {
            return true;
        }

        return word.EndsWith('s') && word.Length > 1 && KnownVerbs.Contains(word[..^1]);
    }

    private static string DescribeReturn(string returnType)
    {
        // A trailing return type names a computation rather than a type. Cutting it at its first
        // angle bracket produced fragments of the expression: `decltype(factory(std::forward<...`
        // was reported as "the resulting forward", and an SFINAE constraint as "the resulting
        // enable_if_t". Neither is a type the caller receives.
        var declared = returnType?.Trim() ?? string.Empty;
        if (declared.StartsWith("decltype(", StringComparison.Ordinal))
        {
            return "a value whose type is deduced from the arguments";
        }

        if (declared.Contains("enable_if", StringComparison.Ordinal))
        {
            return "a value of the type this overload is constrained to";
        }

        var simple = NormalizeType(declared);

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
        // Trimmed again after the suffixes: "Value &" left a trailing space that reached the
        // rendered sentence as "the resulting Value .".
        var simple = type.Trim().TrimEnd('&', '*', '?').Trim();
        var angle = simple.IndexOf('<', StringComparison.Ordinal);
        if (angle > 0)
        {
            simple = simple[..angle].Trim();
        }

        return TypeNames.StripQualifiers(simple);
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
               !type.Contains('<', StringComparison.Ordinal);
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

using System.Text.RegularExpressions;

namespace JBU.CodeLens.Core.Analysis;

/// <summary>
/// Infers postconditions and observable side effects from return statements and mutations.
/// </summary>
public sealed class PostconditionAnalyzer
{
    private readonly RuleEngine<MethodPostcondition> _postconditionEngine;
    private readonly RuleEngine<StateChange> _stateChangeEngine;

    public PostconditionAnalyzer()
    {
        _postconditionEngine = new RuleEngine<MethodPostcondition>()
            .Register("post-name-pattern", "Method name semantic patterns", RuleMethodNamePatterns)
            .Register("post-return-type", "Return type based descriptions", RuleReturnTypeBased)
            .Register("post-throw-present", "Method may throw", RuleThrowPresent)
            .Register("post-state-mutation", "Internal state modified", RuleStateMutationPostcondition)
            .Register("post-async", "Async method", RuleAsyncMethod)
            .Register("post-constructor", "Constructor behavior", RuleConstructor)
            .Register("post-loop-result", "Loop produces result", RuleLoopResult)
            .Register("post-console-side-effect", "Console output side effect", RuleConsolePostcondition)
            .Register("post-power-sqrt", "Math operation result", RuleMathOperation);

        _stateChangeEngine = new RuleEngine<StateChange>()
            .Register("field-increment", "field++ / ++field", RuleFieldIncrement)
            .Register("field-assignment", "field assignment", RuleFieldAssignment)
            .Register("collection-modify", "collection Add/Remove/Push/Pop", RuleCollectionModify)
            .Register("console-output", "Console / cout output", RuleConsoleOutput)
            .Register("file-write", "file write operations", RuleFileWrite)
            .Register("database-write", "database write operations", RuleDatabaseWrite)
            .Register("network-call", "network / HTTP calls", RuleNetworkCall)
            .Register("async-state", "Async execution", RuleAsyncStateChange)
            .Register("exception-swallowed", "Exception caught and ignored", RuleExceptionSwallowed);
    }

    public IReadOnlyList<MethodPostcondition> AnalyzePostconditions(MethodAnalysisContext context) =>
        AnalysisMessageBuilder.DeduplicatePostconditions(_postconditionEngine.EvaluateAll(context));

    public IReadOnlyList<StateChange> AnalyzeStateChanges(MethodAnalysisContext context) =>
        _stateChangeEngine.EvaluateAll(context);

    public IReadOnlyList<AnalysisRule<MethodPostcondition>> PostconditionRules => _postconditionEngine.Rules;
    public IReadOnlyList<AnalysisRule<StateChange>> StateChangeRules => _stateChangeEngine.Rules;

    private static IEnumerable<MethodPostcondition> RuleMethodNamePatterns(MethodAnalysisContext context)
    {
        var methodName = context.Method.Name;
        var returnType = context.Method.ReturnType;
        var parameters = SourcePatternHelpers.ParseParameters(context.Method).ToList();

        if (NameMatches(methodName, "Divide", "Div"))
        {
            if (parameters.Count >= 2)
            {
                yield return CreatePost(
                    AnalysisMessageBuilder.PostDivide(parameters[0].Name, parameters[1].Name, returnType),
                    "post-name-pattern");
            }
            else
            {
                yield return CreatePost(AnalysisMessageBuilder.PostDivideGeneric(), "post-name-pattern");
            }

            yield break;
        }

        if (NameMatches(methodName, "Multiply", "Mul"))
        {
            yield return CreatePost(AnalysisMessageBuilder.PostMultiply(), "post-name-pattern");
            yield break;
        }

        if (NameMatches(methodName, "Subtract", "Sub") && !NameMatches(methodName, "Subscription"))
        {
            yield return CreatePost(AnalysisMessageBuilder.PostSubtract(), "post-name-pattern");
            yield break;
        }

        if (NameMatches(methodName, "Add", "Sum") && !NameMatches(methodName, "Address"))
        {
            yield return CreatePost(AnalysisMessageBuilder.PostAddOrSum(), "post-name-pattern");
            yield break;
        }

        if (methodName.StartsWith("Get", StringComparison.OrdinalIgnoreCase))
        {
            yield return CreatePost(AnalysisMessageBuilder.PostGet(), "post-name-pattern");
            yield break;
        }

        if (methodName.StartsWith("Set", StringComparison.OrdinalIgnoreCase) ||
            methodName.StartsWith("Update", StringComparison.OrdinalIgnoreCase))
        {
            yield return CreatePost(AnalysisMessageBuilder.PostSetOrUpdate(), "post-name-pattern");
            yield break;
        }

        if (methodName.StartsWith("Add", StringComparison.OrdinalIgnoreCase) ||
            methodName.StartsWith("Insert", StringComparison.OrdinalIgnoreCase))
        {
            yield return CreatePost(AnalysisMessageBuilder.PostAddOrInsert(), "post-name-pattern");
            yield break;
        }

        if (methodName.StartsWith("Remove", StringComparison.OrdinalIgnoreCase) ||
            methodName.StartsWith("Delete", StringComparison.OrdinalIgnoreCase))
        {
            yield return CreatePost(AnalysisMessageBuilder.PostRemoveOrDelete(), "post-name-pattern");
            yield break;
        }

        if (methodName.StartsWith("Save", StringComparison.OrdinalIgnoreCase) ||
            methodName.StartsWith("Write", StringComparison.OrdinalIgnoreCase))
        {
            yield return CreatePost(AnalysisMessageBuilder.PostSaveOrWrite(), "post-name-pattern");
            yield break;
        }

        if (methodName.StartsWith("Load", StringComparison.OrdinalIgnoreCase) ||
            methodName.StartsWith("Read", StringComparison.OrdinalIgnoreCase))
        {
            yield return CreatePost(AnalysisMessageBuilder.PostLoadOrRead(), "post-name-pattern");
            yield break;
        }

        if (methodName.StartsWith("Calculate", StringComparison.OrdinalIgnoreCase) ||
            methodName.StartsWith("Compute", StringComparison.OrdinalIgnoreCase))
        {
            yield return CreatePost(AnalysisMessageBuilder.PostCalculateOrCompute(), "post-name-pattern");
            yield break;
        }

        if (methodName.StartsWith("Is", StringComparison.OrdinalIgnoreCase) ||
            methodName.StartsWith("Has", StringComparison.OrdinalIgnoreCase) ||
            methodName.StartsWith("Can", StringComparison.OrdinalIgnoreCase))
        {
            yield return CreatePost(AnalysisMessageBuilder.PostIsHasCan(), "post-name-pattern");
        }
    }

    private static IEnumerable<MethodPostcondition> RuleReturnTypeBased(MethodAnalysisContext context)
    {
        if (NamePatternMatched(context.Method.Name))
        {
            yield break;
        }

        var returnType = context.Method.ReturnType;
        if (IsVoidReturn(returnType))
        {
            yield return CreatePost(AnalysisMessageBuilder.PostVoidAction(), "post-return-type");
            yield break;
        }

        if (returnType.Equals("bool", StringComparison.OrdinalIgnoreCase))
        {
            yield return CreatePost(AnalysisMessageBuilder.PostBoolResult(), "post-return-type");
            yield break;
        }

        if (SourcePatternHelpers.IsFloatingType(returnType) || SourcePatternHelpers.IsNumericType(returnType))
        {
            yield return CreatePost(AnalysisMessageBuilder.PostNumericResult(returnType), "post-return-type");
            yield break;
        }

        if (SourcePatternHelpers.IsStringType(returnType))
        {
            yield return CreatePost(AnalysisMessageBuilder.PostStringResult(), "post-return-type");
            yield break;
        }

        if (SourcePatternHelpers.IsCollectionType(returnType))
        {
            yield return CreatePost(AnalysisMessageBuilder.PostCollectionResult(), "post-return-type");
            yield break;
        }

        if (returnType.Equals("int", StringComparison.OrdinalIgnoreCase) &&
            (context.Method.Name.Contains("Count", StringComparison.OrdinalIgnoreCase) ||
             context.Method.Name.StartsWith("Get", StringComparison.OrdinalIgnoreCase)))
        {
            yield return CreatePost(AnalysisMessageBuilder.PostCountOrGetInt(), "post-return-type");
            yield break;
        }

        if (context.HasSourceBody && SafeRegex.IsMatch(context.SourceBody, @"\breturn\b"))
        {
            yield return CreatePost(
                $"Returns a computed {AnalysisMessageBuilder.NormalizeTypeName(returnType)} result based on the method inputs",
                "post-return-type",
                AnalysisConfidence.Medium);
        }
    }

    private static IEnumerable<MethodPostcondition> RuleThrowPresent(MethodAnalysisContext context)
    {
        var hasThrow = context.Method.ThrownExceptions.Count > 0 ||
                       (context.HasSourceBody && SourcePatternHelpers.ContainsThrow(context.SourceBody));

        if (!hasThrow)
        {
            yield break;
        }

        yield return CreatePost(AnalysisMessageBuilder.PostMayThrow(), "post-throw-present");
    }

    private static IEnumerable<MethodPostcondition> RuleStateMutationPostcondition(MethodAnalysisContext context)
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
                yield return CreatePost(AnalysisMessageBuilder.PostStateMutation(), "post-state-mutation");
                yield break;
            }
        }
    }

    private static IEnumerable<MethodPostcondition> RuleAsyncMethod(MethodAnalysisContext context)
    {
        var methodName = context.Method.Name;
        var returnType = context.Method.ReturnType;
        if (!methodName.Contains("Async", StringComparison.OrdinalIgnoreCase) &&
            !returnType.Contains("Task", StringComparison.OrdinalIgnoreCase))
        {
            yield break;
        }

        yield return CreatePost(
            "Executes asynchronously — the caller must await this method to observe the result",
            "post-async",
            AnalysisConfidence.Medium);
    }

    private static IEnumerable<MethodPostcondition> RuleConstructor(MethodAnalysisContext context)
    {
        var className = context.Method.ParentClass?.Name;
        if (string.IsNullOrEmpty(className) ||
            !context.Method.Name.Equals(className, StringComparison.Ordinal))
        {
            yield break;
        }

        yield return CreatePost(
            "Initializes a new instance of the object with the provided parameter values",
            "post-constructor",
            AnalysisConfidence.High);
    }

    private static IEnumerable<MethodPostcondition> RuleLoopResult(MethodAnalysisContext context)
    {
        if (!context.HasSourceBody || IsVoidReturn(context.Method.ReturnType))
        {
            yield break;
        }

        if (!SafeRegex.IsMatch(context.SourceBody, @"\bforeach\b|\bfor\s*\(|\bwhile\s*\(", RegexOptions.IgnoreCase))
        {
            yield break;
        }

        yield return CreatePost(
            "Processes multiple elements and returns an aggregated or transformed result",
            "post-loop-result",
            AnalysisConfidence.Medium);
    }

    private static IEnumerable<MethodPostcondition> RuleConsolePostcondition(MethodAnalysisContext context)
    {
        if (!context.HasSourceBody)
        {
            yield break;
        }

        if (!context.SourceBody.Contains("Console.", StringComparison.Ordinal) &&
            !context.SourceBody.Contains("std::cout", StringComparison.Ordinal) &&
            !context.SourceBody.Contains("printf(", StringComparison.Ordinal))
        {
            yield break;
        }

        yield return CreatePost(
            "Produces visible output to the console as a side effect of execution",
            "post-console-side-effect",
            AnalysisConfidence.High);
    }

    private static IEnumerable<MethodPostcondition> RuleMathOperation(MethodAnalysisContext context)
    {
        if (!context.HasSourceBody)
        {
            yield break;
        }

        var returnType = context.Method.ReturnType;
        if (!SourcePatternHelpers.IsFloatingType(returnType))
        {
            yield break;
        }

        if (!SafeRegex.IsMatch(context.SourceBody, @"\bMath\.Pow\s*\(|\bMath\.Sqrt\s*\(|\bpow\s*\(|\bsqrt\s*\(|\bstd::pow\s*\(|\bstd::sqrt\s*\(", RegexOptions.IgnoreCase))
        {
            yield break;
        }

        yield return CreatePost(
            "Returns a mathematically computed numeric result — may return NaN for invalid inputs",
            "post-power-sqrt",
            AnalysisConfidence.High);
    }

    private static IEnumerable<StateChange> RuleFieldIncrement(MethodAnalysisContext context)
    {
        if (!context.HasSourceBody)
        {
            yield break;
        }

        var fields = context.Method.ParentClass?.Fields ?? [];
        foreach (var field in fields)
        {
            var pattern = $@"\b{Regex.Escape(field.Name)}\s*(\+\+|--)|(\+\+|--)\s*{Regex.Escape(field.Name)}\b";
            if (!SafeRegex.IsMatch(context.SourceBody, pattern))
            {
                continue;
            }

            yield return new StateChange
            {
                Kind = StateChangeKind.ObjectStateModified,
                Subject = field.Name,
                Description = $"Internal field {field.Name} is incremented or decremented by this method",
                Confidence = AnalysisConfidence.High,
                Reason = "Derived from field increment or decrement operation.",
                RuleId = "field-increment",
            };
        }
    }

    private static IEnumerable<StateChange> RuleFieldAssignment(MethodAnalysisContext context)
    {
        if (!context.HasSourceBody)
        {
            yield break;
        }

        var fields = context.Method.ParentClass?.Fields ?? [];
        foreach (var field in fields)
        {
            var pattern = $@"\b{Regex.Escape(field.Name)}\s*=(?!=)";
            if (!SafeRegex.IsMatch(context.SourceBody, pattern))
            {
                continue;
            }

            yield return new StateChange
            {
                Kind = StateChangeKind.FieldModified,
                Subject = field.Name,
                Description = $"Internal field {field.Name} is assigned a new value by this method",
                Confidence = AnalysisConfidence.High,
                Reason = "Derived from field assignment in method body.",
                RuleId = "field-assignment",
            };
        }
    }

    private static IEnumerable<StateChange> RuleCollectionModify(MethodAnalysisContext context)
    {
        if (!context.HasSourceBody)
        {
            yield break;
        }

        var patterns = new[]
        {
            @"\.Add\s*\(",
            @"\.Remove\s*\(",
            @"\.Push_back\s*\(",
            @"\.Pop_back\s*\(",
            @"\.Insert\s*\(",
            @"\.Erase\s*\(",
        };

        foreach (var pattern in patterns)
        {
            if (!SafeRegex.IsMatch(context.SourceBody, pattern))
            {
                continue;
            }

            yield return new StateChange
            {
                Kind = StateChangeKind.CollectionModified,
                Description = "A collection owned or passed into this method is modified",
                Confidence = AnalysisConfidence.High,
                Reason = "Derived from collection mutation call.",
                RuleId = "collection-modify",
            };
            yield break;
        }
    }

    private static IEnumerable<StateChange> RuleConsoleOutput(MethodAnalysisContext context)
    {
        if (!context.HasSourceBody)
        {
            yield break;
        }

        if (context.SourceBody.Contains("Console.", StringComparison.Ordinal) ||
            context.SourceBody.Contains("std::cout", StringComparison.Ordinal) ||
            context.SourceBody.Contains("printf(", StringComparison.Ordinal))
        {
            yield return new StateChange
            {
                Kind = StateChangeKind.ConsoleOutput,
                Description = "Writes output to the console during method execution",
                Confidence = AnalysisConfidence.High,
                Reason = "Derived from console output API usage.",
                RuleId = "console-output",
            };
        }
    }

    private static IEnumerable<StateChange> RuleFileWrite(MethodAnalysisContext context)
    {
        if (!context.HasSourceBody)
        {
            yield break;
        }

        if (context.SourceBody.Contains("File.Write", StringComparison.Ordinal) ||
            context.SourceBody.Contains("File.Open", StringComparison.Ordinal) ||
            context.SourceBody.Contains("fstream", StringComparison.Ordinal) ||
            context.SourceBody.Contains("ofstream", StringComparison.Ordinal) ||
            context.SourceBody.Contains("fopen(", StringComparison.Ordinal))
        {
            yield return new StateChange
            {
                Kind = StateChangeKind.FileWrite,
                Description = "Performs a file write or open-for-write operation on disk",
                Confidence = AnalysisConfidence.Medium,
                Reason = "Derived from file I/O API usage.",
                RuleId = "file-write",
            };
        }
    }

    private static IEnumerable<StateChange> RuleDatabaseWrite(MethodAnalysisContext context)
    {
        if (!context.HasSourceBody)
        {
            yield break;
        }

        if (SafeRegex.IsMatch(context.SourceBody, @"\b(ExecuteNonQuery|SaveChanges|INSERT\s+INTO|UPDATE\s+\w+)\b", RegexOptions.IgnoreCase))
        {
            yield return new StateChange
            {
                Kind = StateChangeKind.DatabaseWrite,
                Description = "Performs a database write operation that persists changed data",
                Confidence = AnalysisConfidence.Medium,
                Reason = "Derived from database mutation API usage.",
                RuleId = "database-write",
            };
        }
    }

    private static IEnumerable<StateChange> RuleNetworkCall(MethodAnalysisContext context)
    {
        if (!context.HasSourceBody)
        {
            yield break;
        }

        if (context.SourceBody.Contains("HttpClient", StringComparison.Ordinal) ||
            context.SourceBody.Contains("WebRequest", StringComparison.Ordinal) ||
            context.SourceBody.Contains("curl_", StringComparison.Ordinal) ||
            context.SourceBody.Contains("socket(", StringComparison.Ordinal))
        {
            yield return new StateChange
            {
                Kind = StateChangeKind.NetworkCall,
                Description = "Makes a network call to an external service or remote host",
                Confidence = AnalysisConfidence.Medium,
                Reason = "Derived from network API usage.",
                RuleId = "network-call",
            };
        }
    }

    private static IEnumerable<StateChange> RuleAsyncStateChange(MethodAnalysisContext context)
    {
        if (!context.HasSourceBody || !context.SourceBody.Contains("await ", StringComparison.Ordinal))
        {
            yield break;
        }

        yield return new StateChange
        {
            Kind = StateChangeKind.NetworkCall,
            Description = "Awaits one or more asynchronous operations during execution",
            Confidence = AnalysisConfidence.Medium,
            Reason = "Derived from await usage in method body.",
            RuleId = "async-state",
        };
    }

    private static IEnumerable<StateChange> RuleExceptionSwallowed(MethodAnalysisContext context)
    {
        if (!context.HasSourceBody || !SourcePatternHelpers.HasCatchWithoutRethrow(context.SourceBody))
        {
            yield break;
        }

        yield return new StateChange
        {
            Kind = StateChangeKind.ObjectStateModified,
            Description = "Catches exceptions internally — some error conditions may be silently handled",
            Confidence = AnalysisConfidence.Medium,
            Reason = "Derived from catch block that does not rethrow.",
            RuleId = "exception-swallowed",
        };
    }

    private static MethodPostcondition CreatePost(
        string description,
        string ruleId,
        AnalysisConfidence confidence = AnalysisConfidence.High) =>
        new()
        {
            Description = description,
            Confidence = confidence,
            Reason = "Derived from method name or return type rule.",
            RuleId = ruleId,
        };

    private static bool NameMatches(string methodName, params string[] tokens) =>
        tokens.Any(token => methodName.Contains(token, StringComparison.OrdinalIgnoreCase));

    private static bool NamePatternMatched(string methodName)
    {
        if (NameMatches(methodName, "Divide", "Div", "Multiply", "Mul", "Subtract", "Sub", "Sum"))
        {
            return true;
        }

        foreach (var prefix in new[] { "Get", "Set", "Update", "Add", "Insert", "Remove", "Delete", "Save", "Write", "Load", "Read", "Calculate", "Compute", "Is", "Has", "Can" })
        {
            if (methodName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsVoidReturn(string returnType) =>
        returnType.Equals("void", StringComparison.OrdinalIgnoreCase);
}

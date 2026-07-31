namespace JBU.CodeLens.Shared.Models;

/// <summary>
/// Confidence assigned to a deterministic inference.
/// </summary>
public enum AnalysisConfidence
{
    High,
    Medium,
    Low,
}

/// <summary>
/// How a symbol is used inside a method body.
/// </summary>
public enum VariableUsageKind
{
    Unused,
    Read,
    Written,
    ReadWrite,
}

/// <summary>
/// Where a tracked symbol is declared.
/// </summary>
public enum VariableScopeKind
{
    Parameter,
    Local,
    Field,
    Property,
}

/// <summary>
/// Categories of design constraints inferred from method behavior.
/// </summary>
public enum DesignConstraintKind
{
    Pure,
    Impure,
    Stateful,
    Stateless,
    UsesConsole,
    UsesFiles,
    UsesDatabase,
    UsesNetwork,
    UsesStaticMembers,
    UsesRecursion,
    UsesThreadSynchronization,
}

/// <summary>
/// Categories of observable state or I/O side effects.
/// </summary>
public enum StateChangeKind
{
    ReturnValue,
    FieldModified,
    CollectionModified,
    ObjectStateModified,
    ConsoleOutput,
    FileWrite,
    DatabaseWrite,
    NetworkCall,
}

/// <summary>
/// Aggregated, language-independent analysis for a single method.
/// </summary>
public sealed class MethodAnalysis
{
    public string MethodName { get; set; } = string.Empty;
    public string? SourceFilePath { get; set; }
    public string Language { get; set; } = "Unknown";

    public List<MethodPrecondition> Preconditions { get; set; } = new();
    public List<MethodPostcondition> Postconditions { get; set; } = new();
    public List<VariableAnalysis> Variables { get; set; } = new();
    public List<VariableLimit> VariableLimits { get; set; } = new();
    public List<RuntimeRisk> RuntimeRisks { get; set; } = new();
    public List<DesignConstraint> DesignConstraints { get; set; } = new();
    public List<DependencyInfo> Dependencies { get; set; } = new();
    public List<StateChange> StateChanges { get; set; } = new();
    public List<ExecutionStep> ExecutionSteps { get; set; } = new();
}

public sealed class MethodPrecondition
{
    public string Description { get; set; } = string.Empty;
    public string? Subject { get; set; }
    public AnalysisConfidence Confidence { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string? RuleId { get; set; }
}

public sealed class MethodPostcondition
{
    public string Description { get; set; } = string.Empty;
    public string? Subject { get; set; }
    public AnalysisConfidence Confidence { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string? RuleId { get; set; }
}

public sealed class RuntimeRisk
{
    public string Description { get; set; } = string.Empty;
    public string? ExceptionType { get; set; }
    public AnalysisConfidence Confidence { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string? RuleId { get; set; }
}

public sealed class VariableAnalysis
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public VariableScopeKind Scope { get; set; }
    public VariableUsageKind Usage { get; set; }
    public AnalysisConfidence Confidence { get; set; }
    public string Reason { get; set; } = string.Empty;
}

public sealed class StateChange
{
    public StateChangeKind Kind { get; set; }
    public string Description { get; set; } = string.Empty;
    public string? Subject { get; set; }
    public AnalysisConfidence Confidence { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string? RuleId { get; set; }
}

public sealed class DesignConstraint
{
    public DesignConstraintKind Kind { get; set; }
    public string Description { get; set; } = string.Empty;
    public AnalysisConfidence Confidence { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string? RuleId { get; set; }
}

public sealed class DependencyInfo
{
    public string Name { get; set; } = string.Empty;
    public DependencyKind Kind { get; set; }
    public string? NamespaceOrType { get; set; }
    public AnalysisConfidence Confidence { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string? RuleId { get; set; }
}

public enum DependencyKind
{
    MethodCall,
    ExternalApi,
    Namespace,
    ReferencedClass,
}

/// <summary>
/// Read-only view of parser output used by all analyzers.
/// </summary>
public sealed class MethodAnalysisContext
{
    public MethodInfo Method { get; }

    /// <summary>
    /// Method body text when the parser stored it (typically C++). May be empty for C#.
    /// </summary>
    public string SourceBody { get; }

    public string Language { get; }

    public bool HasSourceBody => SourceBody.Length > 0;

    public MethodAnalysisContext(MethodInfo method)
    {
        ArgumentNullException.ThrowIfNull(method);

        Method = method;
        Method.XmlDocTags.TryGetValue("sourceCode", out var source);
        SourceBody = source?.Trim() ?? string.Empty;
        Language = DetectLanguage(method);
    }

    private static string DetectLanguage(MethodInfo method)
    {
        var path = method.ParentClass?.SourceFilePath;
        if (string.IsNullOrEmpty(path))
        {
            return "Unknown";
        }

        if (LanguageFileExtensions.IsCppFile(path))
        {
            return "C++";
        }

        if (LanguageFileExtensions.IsCSharpFile(path))
        {
            return "C#";
        }

        return "Unknown";
    }
}

/// <summary>
/// One numbered step in a method's inferred execution flow.
/// </summary>
public sealed class ExecutionStep
{
    public int StepNumber { get; set; }
    public string Description { get; set; } = string.Empty;
    public ExecutionStepKind Kind { get; set; }
}

/// <summary>
/// The phase of execution an <see cref="ExecutionStep"/> belongs to.
/// </summary>
public enum ExecutionStepKind
{
    Validation,
    Initialization,
    Calculation,
    StateUpdate,
    ExternalCall,
    LoopProcessing,
    ExceptionHandling,
    ConsoleOutput,
    FileOperation,
    DatabaseOperation,
    ReturnResult,
    Delegation,
}

/// <summary>
/// The range of values a single variable is allowed to hold inside one method, as far as the
/// method's own code reveals it — for example "0 to 100" from a guard clause, or "'a' to 'z'"
/// from a character comparison.
/// </summary>
/// <remarks>
/// This is deliberately narrower than <see cref="MethodPrecondition"/>. A precondition says what
/// must be true before the method runs; a limit says which values the variable may take, so it
/// can be shown beside the variable rather than as prose.
/// </remarks>
public sealed class VariableLimit
{
    public string Name { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    /// <summary>Where the variable is declared, so the reader knows what they are looking at.</summary>
    public VariableScopeKind Scope { get; set; }

    /// <summary>
    /// The allowed values, already written for a reader: "0 to 100", "'a' to 'z'",
    /// "must not be null, at most 50 characters".
    /// </summary>
    public string Limit { get; set; } = string.Empty;

    /// <summary>
    /// What the limit constrains. A variable can be subject to several at once — a string may
    /// have to be present <em>and</em> short — and those are merged into one statement rather
    /// than competing, since they are complementary facts rather than rival claims.
    /// </summary>
    public VariableLimitKind Kind { get; set; }

    /// <summary>The line of code the limit was read from, so the claim can be checked.</summary>
    public string Evidence { get; set; } = string.Empty;

    /// <summary>What kind of code established the limit.</summary>
    public VariableLimitSource Source { get; set; }

    public AnalysisConfidence Confidence { get; set; }
}

/// <summary>
/// What aspect of a variable a limit constrains. Two limits of different kinds describe different
/// things and are both true at once; two of the same kind are rival claims, and only the
/// better-evidenced one is kept.
/// </summary>
public enum VariableLimitKind
{
    /// <summary>The span of values permitted, such as "1 to 100" or "greater than 0".</summary>
    Range,

    /// <summary>How much the value may hold, in characters or items.</summary>
    Size,

    /// <summary>Whether the value may be absent.</summary>
    Presence,

    /// <summary>The specific values permitted, where only a few are.</summary>
    Membership,

    /// <summary>What the declared type permits, when nothing narrower was found.</summary>
    DeclaredType,
}

/// <summary>
/// What established a <see cref="VariableLimit"/>. Ordered strongest first: a value rejected by a
/// guard is a harder fact than one merely implied by the variable's type.
/// </summary>
public enum VariableLimitSource
{
    /// <summary>A check that throws or returns when the value falls outside the range.</summary>
    Guard,

    /// <summary>A call that forces the value into a range, such as Math.Clamp.</summary>
    Clamp,

    /// <summary>A comparison that the surrounding code depends on.</summary>
    Comparison,

    /// <summary>The start and end of a counting loop.</summary>
    LoopBound,

    /// <summary>The natural range of the declared type, with nothing narrower found.</summary>
    DeclaredType,
}
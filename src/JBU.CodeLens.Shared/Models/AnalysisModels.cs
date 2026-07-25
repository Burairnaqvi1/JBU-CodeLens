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
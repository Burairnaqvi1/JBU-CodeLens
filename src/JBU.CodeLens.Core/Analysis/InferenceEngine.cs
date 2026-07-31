namespace JBU.CodeLens.Core.Analysis;

/// <summary>
/// Coordinates deterministic analyzers that derive <see cref="MethodAnalysis"/> from parser output.
/// Sits between language parsers and the UI; does not use AI.
/// </summary>
public sealed class InferenceEngine
{
    private readonly PreconditionAnalyzer _preconditionAnalyzer = new();
    private readonly PostconditionAnalyzer _postconditionAnalyzer = new();
    private readonly VariableAnalyzer _variableAnalyzer = new();
    private readonly VariableLimitAnalyzer _variableLimitAnalyzer = new();
    private readonly RuntimeRiskAnalyzer _runtimeRiskAnalyzer = new();
    private readonly DesignConstraintAnalyzer _designConstraintAnalyzer = new();
    private readonly DependencyAnalyzer _dependencyAnalyzer = new();
    private readonly ExecutionFlowAnalyzer _executionFlowAnalyzer = new();

    /// <summary>
    /// Runs all registered analyzers and merges their output into a single <see cref="MethodAnalysis"/>.
    /// </summary>
    public MethodAnalysis Analyze(MethodInfo method)
    {
        ArgumentNullException.ThrowIfNull(method);

        var context = new MethodAnalysisContext(method);

        var analysis = new MethodAnalysis
        {
            MethodName = method.Name,
            SourceFilePath = method.ParentClass?.SourceFilePath,
            Language = context.Language,
            Preconditions = _preconditionAnalyzer.Analyze(context).ToList(),
            Postconditions = _postconditionAnalyzer.AnalyzePostconditions(context).ToList(),
            StateChanges = _postconditionAnalyzer.AnalyzeStateChanges(context).ToList(),
            Variables = _variableAnalyzer.Analyze(context).ToList(),
            VariableLimits = _variableLimitAnalyzer.Analyze(context).ToList(),
            RuntimeRisks = _runtimeRiskAnalyzer.Analyze(context).ToList(),
            DesignConstraints = _designConstraintAnalyzer.Analyze(context).ToList(),
            Dependencies = _dependencyAnalyzer.Analyze(context).ToList(),
            ExecutionSteps = _executionFlowAnalyzer.Analyze(context).ToList(),
        };

        return analysis;
    }

    /// <summary>
    /// Exposes registered analyzers for diagnostics and future extension.
    /// </summary>
    public InferenceEngineCapabilities Capabilities => new(
        _preconditionAnalyzer,
        _postconditionAnalyzer,
        _variableAnalyzer,
        _variableLimitAnalyzer,
        _runtimeRiskAnalyzer,
        _designConstraintAnalyzer,
        _dependencyAnalyzer);
}

/// <summary>
/// Read-only view of analyzer instances and their registered rules.
/// </summary>
public sealed class InferenceEngineCapabilities
{
    public PreconditionAnalyzer Preconditions { get; }
    public PostconditionAnalyzer Postconditions { get; }
    public VariableAnalyzer Variables { get; }
    public VariableLimitAnalyzer VariableLimits { get; }
    public RuntimeRiskAnalyzer RuntimeRisks { get; }
    public DesignConstraintAnalyzer DesignConstraints { get; }
    public DependencyAnalyzer Dependencies { get; }

    internal InferenceEngineCapabilities(
        PreconditionAnalyzer preconditions,
        PostconditionAnalyzer postconditions,
        VariableAnalyzer variables,
        VariableLimitAnalyzer variableLimits,
        RuntimeRiskAnalyzer runtimeRisks,
        DesignConstraintAnalyzer designConstraints,
        DependencyAnalyzer dependencies)
    {
        Preconditions = preconditions;
        Postconditions = postconditions;
        Variables = variables;
        VariableLimits = variableLimits;
        RuntimeRisks = runtimeRisks;
        DesignConstraints = designConstraints;
        Dependencies = dependencies;
    }
}

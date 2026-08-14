namespace JBU.CodeLens.Shared.Structural;

/// <summary>
/// Everything a project scan produces: the per-file parse results that back the UI tree, the
/// structural IR and knowledge graph, metrics, lookup indexes, and scan statistics.
/// </summary>
public class AnalysisResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }

    /// <summary>
    /// True when the scan stopped because the folder holds no source files this tool reads.
    /// </summary>
    /// <remarks>
    /// Distinguished from the other unsuccessful outcomes because it is not a failure: nothing
    /// went wrong, the folder simply is not a C#/C++ project. The UI reports it as a plain
    /// result rather than an error, and needs to tell the two apart without matching on the
    /// wording of <see cref="Error"/>.
    /// </remarks>
    public bool NoSourceFiles { get; set; }
    public ProjectIR? Ir { get; set; }
    public KnowledgeGraph? Graph { get; set; }
    public MetricsResult? Metrics { get; set; }
    public List<ParseResult> ParseResults { get; set; } = [];
    public Dictionary<string, MethodInfo> ScideMethodIndex { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, TypeInfo> ScideTypeIndex { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public int AnalyzedFiles { get; set; }
    public List<string> FailedFiles { get; set; } = [];
    public int ClassCount { get; set; }
    public int MethodCount { get; set; }
}

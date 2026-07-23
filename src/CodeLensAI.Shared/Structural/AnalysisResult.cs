namespace CodeLensAI.Shared.Structural;

/// <summary>
/// Everything a project scan produces: the per-file parse results that back the UI tree, the
/// structural IR and knowledge graph, metrics, lookup indexes, and scan statistics.
/// </summary>
public class AnalysisResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
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

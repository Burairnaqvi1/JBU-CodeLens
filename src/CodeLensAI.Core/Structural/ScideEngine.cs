using CodeLensAI.Core;
using CodeLensAI.Core.Analysis;

namespace CodeLensAI.Core.Structural;

/// <summary>
/// Single entry point for project-wide structural analysis (relationships, call graph, metrics,
/// knowledge graph) layered on top of CodeLensAI's own parsers. Scans and parses each source file
/// exactly once — the resulting <see cref="ParseResult"/>/<see cref="ClassInfo"/> trees back both
/// the UI's file/class tree and the <see cref="ProjectIR"/> used for structural analysis, so a
/// project scan no longer parses every file twice.
/// </summary>
public sealed class ScideEngine
{
    private readonly CSharpParser _csharpParser = new();
    private readonly CppParser _cppParser = new();
    private readonly InferenceEngine _inferenceEngine = new();
    private readonly SymbolTable _symbolTable = new();
    private readonly RelationshipExtractor _relationshipExtractor = new();
    private readonly CallGraphBuilder _callGraphBuilder = new();
    private readonly MetricsCalculator _metricsCalculator = new();

    public AnalysisResult AnalyzeProject(string path)
    {
        var result = new AnalysisResult();

        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
        {
            result.Error = "Invalid project path";
            return result;
        }

        try
        {
            var filePaths = DirectoryScanner.ScanForSourceFiles(path).ToList();
            if (filePaths.Count == 0)
            {
                result.Error = "No C# or C++ source files found";
                return result;
            }

            var parseResults = new List<ParseResult>(filePaths.Count);
            var failedFiles = new List<string>();
            var allTypes = new List<TypeInfo>();
            var nsMap = new Dictionary<string, NamespaceInfo>();
            var classCount = 0;
            var methodCount = 0;

            foreach (var filePath in filePaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                var parseResult = LanguageFileExtensions.IsCppFile(filePath)
                    ? _cppParser.Parse(filePath)
                    : _csharpParser.Parse(filePath);

                if (parseResult.Errors.Count > 0)
                {
                    failedFiles.Add(filePath);
                }

                foreach (var classInfo in parseResult.Classes)
                {
                    classInfo.Category = CategoryClassifier.Classify(classInfo);
                    classCount++;

                    foreach (var method in classInfo.Methods)
                    {
                        method.ParentClass = classInfo;
                        method.CachedAnalysis = _inferenceEngine.Analyze(method);
                        methodCount++;
                    }

                    var type = TypeInfoConverter.FromClassInfo(classInfo);
                    allTypes.Add(type);

                    if (!string.IsNullOrEmpty(type.NamespaceName))
                    {
                        if (!nsMap.TryGetValue(type.NamespaceName, out var ns))
                        {
                            ns = new NamespaceInfo { Name = type.NamespaceName };
                            nsMap[type.NamespaceName] = ns;
                        }

                        ns.Classes.Add(type);
                    }
                }

                parseResults.Add(parseResult);
            }

            var ir = new ProjectIR
            {
                ProjectName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                RootPath = Path.GetFullPath(path),
                FilesAnalyzed = filePaths.Count,
                FilesFailed = failedFiles.Count,
                Classes = allTypes,
                Namespaces = nsMap.Values.ToList(),
                Methods = allTypes.SelectMany(t => t.Methods).ToList(),
            };

            foreach (var type in allTypes)
            {
                if (!string.IsNullOrEmpty(type.FullName))
                {
                    ir.TypeIndex[type.FullName] = type;
                }
            }

            _symbolTable.BuildFrom(ir);
            ir.Relationships = _relationshipExtractor.Extract(ir);
            ir.CallGraph = _callGraphBuilder.Build(ir);
            var metrics = _metricsCalculator.Calculate(ir);
            ir.Metrics = metrics;

            var graph = KnowledgeGraph.BuildFrom(ir);

            // Nothing reads the symbol table after analysis completes — release it now rather
            // than holding the whole scan's symbols in memory until the next scan rebuilds it.
            _symbolTable.Clear();

            result.Success = true;
            result.Ir = ir;
            result.Graph = graph;
            result.Metrics = metrics;
            result.ParseResults = parseResults;
            result.ScideMethodIndex = ScideMethodIndex.BuildMethods(ir);
            result.ScideTypeIndex = ScideMethodIndex.BuildTypes(ir);
            result.ClassCount = classCount;
            result.MethodCount = methodCount;
            result.AnalyzedFiles = filePaths.Count - failedFiles.Count;
            result.FailedFiles = failedFiles;

            return result;
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
            return result;
        }
    }

    /// <summary>
    /// Metrics-based project summary text. There is no LLM path here on purpose — SCIDE's own LLM
    /// integration was removed because AI runs exclusively through the single
    /// <see cref="ExplanationService"/> instance owned by the UI; loading a second model would
    /// double memory usage and stall the first call.
    /// </summary>
    public string GetProjectSummaryFallback(ProjectIR ir)
    {
        var m = ir.Metrics;
        return m != null
            ? $"## Project Summary: {ir.ProjectName}\n\n**Metrics:** {m.TotalClasses} classes, {m.TotalMethods} methods, {m.TotalNamespaces} namespaces, MI={m.MaintainabilityIndex:F0}"
            : $"## Project Summary: {ir.ProjectName}";
    }

}

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

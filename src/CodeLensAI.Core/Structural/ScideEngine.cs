using System.Collections.Concurrent;
using CodeLensAI.Core;
using CodeLensAI.Core.Analysis;

namespace CodeLensAI.Core.Structural;

/// <summary>
/// Single entry point for project-wide structural analysis (relationships, call graph, metrics,
/// knowledge graph) layered on top of CodeLensAI's own parsers. Files are parsed in parallel
/// (bounded to half the logical cores, leaving headroom for the UI and LLM threads) and each
/// file's <see cref="ParseResult"/> is cached across scans keyed on its last-write time, so a
/// rescan only re-parses files that actually changed — which also preserves their cached
/// deterministic analysis and AI descriptions.
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

    // Cross-scan parse cache keyed on file path; entries are valid while the file's last-write
    // time is unchanged. Entries for files that leave the scanned set are evicted at scan end.
    private readonly ConcurrentDictionary<string, (DateTime LastWriteUtc, ParseResult Result)> _parseCache =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task<AnalysisResult> AnalyzeProjectAsync(string path, CancellationToken cancellationToken = default)
    {
        var result = new AnalysisResult();

        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
        {
            result.Error = "Invalid project path";
            return result;
        }

        try
        {
            var filePaths = DirectoryScanner.ScanForSourceFiles(path)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (filePaths.Count == 0)
            {
                result.Error = "No C# or C++ source files found";
                return result;
            }

            // Parse in parallel into an index-addressed array: no collection contention, and
            // the deterministic file order survives regardless of task completion order.
            var parsed = new ParseResult[filePaths.Count];
            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2),
                CancellationToken = cancellationToken,
            };
            await Parallel.ForEachAsync(
                Enumerable.Range(0, filePaths.Count),
                parallelOptions,
                async (i, ct) =>
                {
                    parsed[i] = await ParseFileCachedAsync(filePaths[i], ct).ConfigureAwait(false);
                }).ConfigureAwait(false);

            EvictStaleCacheEntries(filePaths);

            var parseResults = new List<ParseResult>(filePaths.Count);
            var failedFiles = new List<string>();
            var allTypes = new List<TypeInfo>();
            var nsMap = new Dictionary<string, NamespaceInfo>();
            var classCount = 0;
            var methodCount = 0;

            foreach (var parseResult in parsed)
            {
                var filePath = parseResult.FilePath;

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
                        // ??= so cache-hit files keep their analysis instead of recomputing it.
                        method.CachedAnalysis ??= _inferenceEngine.Analyze(method);
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
    /// Parses one file, or returns the cached result when the file's last-write time matches the
    /// cached entry. Stat failures fall through to a plain parse (never cached with a bad stamp).
    /// </summary>
    private async Task<ParseResult> ParseFileCachedAsync(string filePath, CancellationToken cancellationToken)
    {
        DateTime lastWriteUtc;
        try
        {
            lastWriteUtc = File.GetLastWriteTimeUtc(filePath);
        }
        catch
        {
            lastWriteUtc = DateTime.MinValue;
        }

        if (lastWriteUtc != DateTime.MinValue &&
            _parseCache.TryGetValue(filePath, out var cached) &&
            cached.LastWriteUtc == lastWriteUtc)
        {
            return cached.Result;
        }

        var parser = (ILanguageParser)(LanguageFileExtensions.IsCppFile(filePath) ? _cppParser : _csharpParser);
        var parseResult = await parser.ParseAsync(filePath, cancellationToken).ConfigureAwait(false);

        if (lastWriteUtc != DateTime.MinValue && parseResult.Errors.Count == 0)
        {
            _parseCache[filePath] = (lastWriteUtc, parseResult);
        }

        return parseResult;
    }

    /// <summary>
    /// Drops cache entries for files that are no longer part of the scanned set, so switching
    /// between projects doesn't accumulate stale parse trees.
    /// </summary>
    private void EvictStaleCacheEntries(List<string> currentFiles)
    {
        var current = new HashSet<string>(currentFiles, StringComparer.OrdinalIgnoreCase);
        foreach (var key in _parseCache.Keys)
        {
            if (!current.Contains(key))
            {
                _parseCache.TryRemove(key, out _);
            }
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

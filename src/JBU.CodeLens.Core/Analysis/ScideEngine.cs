using System.Collections.Concurrent;

using JBU.CodeLens.Shared.Structural;
using MethodInfo = JBU.CodeLens.Shared.Models.MethodInfo;

namespace JBU.CodeLens.Core.Analysis;

/// <summary>
/// Single entry point for project-wide structural analysis (relationships, call graph, metrics,
/// knowledge graph) layered on top of JBU.CodeLens's own parsers. Files are parsed in parallel
/// (bounded to half the logical cores, leaving headroom for the UI and LLM threads) and each
/// file's <see cref="ParseResult"/> is cached across scans keyed on its last-write time, so a
/// rescan only re-parses files that actually changed — which also preserves their cached
/// deterministic analysis and AI descriptions.
/// </summary>
public sealed class ScideEngine : IProjectAnalyzer
{
    private readonly CSharpParser _csharpParser = new();
    private readonly CppParser _cppParser = new();
    private readonly InferenceEngine _inferenceEngine = new();
    private readonly SymbolTable _symbolTable = new();

    // Cross-scan parse cache keyed on file path; entries are valid while the file's last-write
    // time is unchanged. Entries for files that leave the scanned set are evicted at scan end.
    private readonly ConcurrentDictionary<string, (DateTime LastWriteUtc, ParseResult Result)> _parseCache =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task<AnalysisResult> AnalyzeProjectAsync(
        string path,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
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
            var parsedCount = 0;
            await Parallel.ForEachAsync(
                Enumerable.Range(0, filePaths.Count),
                parallelOptions,
                async (i, ct) =>
                {
                    parsed[i] = await ParseFileCachedAsync(filePaths[i], ct).ConfigureAwait(false);
                    var done = Interlocked.Increment(ref parsedCount);
                    progress?.Report(new ScanProgress(done, filePaths.Count, Path.GetFileName(filePaths[i])));
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
                    // ??= so cache-hit files keep the description already built for them.
                    classInfo.InferredDescription ??= ClassDescriptionBuilder.Build(classInfo);
                    classCount++;

                    foreach (var method in classInfo.Methods)
                    {
                        method.ParentClass = classInfo;
                        // ??= so cache-hit files keep their analysis instead of recomputing it.
                        method.CachedAnalysis ??= _inferenceEngine.Analyze(method);
                        // The Roslyn node retains the whole file's syntax tree via parent links;
                        // analysis is complete here, so release it. Anything that later needs
                        // the body (the flow analyzer's fallback) re-parses the stored text.
                        method.SyntaxNode = null;
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
            ir.Relationships = RelationshipExtractor.Extract(ir);
            ir.CallGraph = CallGraphBuilder.Build(ir);
            var metrics = MetricsCalculator.Calculate(ir);
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
        catch (OperationCanceledException)
        {
            result.Error = "Scan canceled.";
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
        ArgumentNullException.ThrowIfNull(ir);

        var m = ir.Metrics;
        return m != null
            ? $"## Project Summary: {ir.ProjectName}\n\n**Metrics:** {m.TotalClasses} classes, {m.TotalMethods} methods, {m.TotalNamespaces} namespaces, MI={m.MaintainabilityIndex:F0}"
            : $"## Project Summary: {ir.ProjectName}";
    }

    /// <summary>
    /// Assembles the detail-panel context for a method: SCIDE IR lookups, deterministic analysis
    /// (from the scan cache, or computed on demand for methods viewed before a scan finishes),
    /// and preformatted display strings so the UI renderer never calls analysis logic itself.
    /// </summary>
    public MethodDetailContext BuildMethodDetailContext(
        MethodInfo method,
        ProjectIR? ir,
        IReadOnlyDictionary<string, JBU.CodeLens.Shared.Structural.MethodInfo> methodIndex,
        IReadOnlyDictionary<string, TypeInfo> typeIndex)
    {
        ArgumentNullException.ThrowIfNull(method);

        var parent = method.ParentClass;
        var scideMethod = parent is not null
            ? ScideMethodIndex.Lookup(methodIndex, parent, method)
            : null;
        var scideType = parent is not null
            ? ScideMethodIndex.LookupType(typeIndex, parent, scideMethod)
            : null;

        var analysis = method.CachedAnalysis ??= _inferenceEngine.Analyze(method);

        return new MethodDetailContext
        {
            Method = method,
            ScideMethod = scideMethod,
            ScideType = scideType,
            Analysis = analysis,
            ProjectIr = ir,
            InferredDescription = MethodDescriptionBuilder.Build(method),
            FormattedOperationalLimits = method.OperationalLimits
                .Select(limit => OperationalLimitFormatter.Format(limit, method))
                .ToList(),
        };
    }
}

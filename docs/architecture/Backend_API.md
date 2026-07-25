# ScideEngine API Reference

The `ScideEngine` class in `JBU.CodeLens.Core.Structural` is the single entry point for project-wide structural analysis (relationships, call graph, metrics, knowledge graph).

## Constructor

```csharp
public ScideEngine()
```

Creates an engine instance. There is no configuration object — AI inference runs exclusively through the single `ExplanationService` instance owned by the UI, not inside the engine.

## Public Methods

### AnalyzeProject

```csharp
public AnalysisResult AnalyzeProject(string path)
```

Scans and parses every C#/C++ source file under a directory exactly once, then layers structural analysis on top.

**Input:** Path to a directory containing source files (C# and/or C++).
**Output:** `AnalysisResult` with:

| Property | Type | Description |
|---|---|---|
| Success | bool | Whether analysis completed |
| Error | string? | Failure reason when `Success` is false |
| Ir | ProjectIR? | Full intermediate representation |
| Graph | KnowledgeGraph? | Knowledge graph built from the IR |
| Metrics | MetricsResult? | Project metrics (complexity, MI, coupling…) |
| ParseResults | List&lt;ParseResult&gt; | Per-file parse results backing the UI tree |
| ScideMethodIndex / ScideTypeIndex | Dictionary | Lookup indices for the detail panel |
| AnalyzedFiles / FailedFiles / ClassCount / MethodCount | — | Scan statistics |

Invalid paths are reported via `Error` rather than thrown exceptions.

### GetProjectSummaryFallback

```csharp
public string GetProjectSummaryFallback(ProjectIR ir)
```

Metrics-based project summary text used when the AI model is unavailable. There is no LLM path inside the engine on purpose — loading a second model would double memory usage.

## Exporting

Export is not part of the engine. Use `InferenceExportHelper` (Markdown/JSON including deterministic per-method analysis) or the lower-level `MarkdownExporter`/`JsonExporter` directly:

```csharp
var engine = new ScideEngine();
var result = engine.AnalyzeProject(@"C:\Projects\MyApp");
if (!result.Success) { Console.WriteLine(result.Error); return; }

InferenceExportHelper.WriteMarkdownFile(result.Ir!, result.ParseResults, "docs.md");
InferenceExportHelper.WriteJsonFile(result.Ir!, result.ParseResults, "docs.json");
```

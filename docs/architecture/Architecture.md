# JBU.CodeLens Architecture

## System Overview

```
┌──────────────────────────────────────────────────────────────┐
│                    JBU.CodeLens.UI (WPF)                        │
│  Views/       MainWindow (composition root: constructs the   │
│               concrete Core services, then uses interfaces)  │
│  Renderers/   DetailPanelRenderer (presentation only)        │
│  Theme/       App.xaml + theme resource dictionaries         │
│  Helpers/     UI-only helpers (CustomFaqStore)               │
└──────────────┬───────────────────────────────────────────────┘
               │ depends on interfaces + DTOs only
               ▼
┌──────────────────────────────────────────────────────────────┐
│                     JBU.CodeLens.Shared                         │
│  Interfaces/  IProjectAnalyzer, IExplanationService,          │
│               IMethodConversationSession, IExportService     │
│  Models/      ClassInfo, MethodInfo, ParseResult,            │
│               MethodAnalysis, MethodAiDocumentation, …       │
│  Structural/  ProjectIR, TypeInfo, KnowledgeGraph,           │
│               AnalysisResult, MethodDetailContext            │
│  Utilities/   LanguageFileExtensions                         │
└──────────────▲───────────────────────────────────────────────┘
               │ implements
┌──────────────┴───────────────────────────────────────────────┐
│                      JBU.CodeLens.Core                          │
│  Parsing/     ILanguageParser; CSharp/ (Roslyn, syntax-only, │
│               single-pass body walk); Cpp/ (libclang P/Invoke,│
│               one TU per file)                               │
│  Analysis/    ScideEngine (IProjectAnalyzer: parallel parse, │
│               cross-scan cache), deterministic analyzers,    │
│               relationship/call-graph/metrics builders       │
│  AI/          ExplanationService (IExplanationService:       │
│               LLamaSharp, persistent context, session cache, │
│               merged 5-in-1 documentation call)              │
│  Export/      Word/Markdown/JSON exporters (IExportService)  │
│  Models/      Core-internal models (SymbolTable)             │
│  Utilities/   DirectoryScanner                               │
└──────────────────────────────────────────────────────────────┘
```

## Dependency rules

- **UI → Shared**: all behavior is consumed through the four Shared interfaces; all data through
  Shared DTOs. `Views/MainWindow.xaml.cs` is the single composition root that references Core
  concrete types (three `new` expressions) — every other UI file compiles against Shared only.
- **Core → Shared**: Core implements the interfaces and populates the DTOs. Core never
  references UI assemblies (no PresentationFramework/WindowsBase in Core or Shared).
- **Shared**: no package references at all. `MethodInfo.SyntaxNode` is typed `object?`
  specifically so Shared carries no Roslyn dependency; Core casts it back to
  `MethodDeclarationSyntax` where needed.
- Name collisions between `Shared.Models` (parser-facing `MethodInfo`/`PropertyInfo`) and
  `Shared.Structural` (IR `MethodInfo`/`PropertyInfo`) are handled by never global-importing
  `Shared.Structural`; files that need IR types import it explicitly, usually behind aliases.

## Scan pipeline (one parse per file, cached across scans)

1. **Enumerate** — `Utilities/DirectoryScanner` walks the project (skips reparse points and
   inaccessible folders, excludes generated/tooling directories).
2. **Parse (parallel, cached)** — `Analysis/ScideEngine.AnalyzeProjectAsync` parses files with
   `Parsing/CSharp/CSharpParser` (Roslyn, purely syntactic, one `DescendantNodes` walk per
   method) or `Parsing/Cpp/CppParser` (libclang, one translation unit per file) via
   `Parallel.ForEachAsync` bounded to half the logical cores. A cross-scan cache keyed on
   (path, last-write-time) makes rescans of unchanged files effectively free.
3. **Deterministic per-method inference** — `Analysis/InferenceEngine` (preconditions,
   postconditions, execution steps, design constraints, runtime risks) attaches to each method
   once (`??=`) and survives rescans of unchanged files.
4. **Structural conversion** — `Analysis/TypeInfoConverter` converts each `ClassInfo` into the
   project-wide `TypeInfo` shape; `RelationshipExtractor`, `CallGraphBuilder`, and
   `MetricsCalculator` run over the resulting `ProjectIR`.
5. **Graph** — `KnowledgeGraph.BuildFrom` constructs typed nodes and edges (CONTAINS, INHERITS,
   IMPLEMENTS, CALLS).
6. **Export** — `Export/MarkdownExporter` and `Export/JsonExporter` serialize `ProjectIR`;
   `Export/InferenceExportHelper` layers deterministic per-method analysis on top;
   `Export/WordExporter` produces full documentation, with AI sections coming from a single
   merged model call per method. All are fronted by `Export/ExportService` (`IExportService`).

There is no LLM step in the scan pipeline. AI explanations run exclusively through the single
`AI/ExplanationService` (LLamaSharp, local GGUF model) constructed by the UI's composition root —
see [LLM.md](LLM.md) for why a second LLM path was removed rather than kept as an option.
The service keeps one `LLamaContext` alive with its KV cache cleared per call, serializes all
inference behind a semaphore, and caches results per session keyed on
(operation, file path, last-write-time, method signature).

## Module Responsibilities

| Location | Responsibility |
|---|---|
| `JBU.CodeLens.Shared/Interfaces/` | The four service contracts the UI consumes |
| `JBU.CodeLens.Shared/Models/` | Parser-facing DTOs (`ClassInfo`, `MethodInfo`, `ParseResult`, analysis models) |
| `JBU.CodeLens.Shared/Structural/` | Project-wide IR DTOs (`ProjectIR`, `TypeInfo`, `KnowledgeGraph`, `AnalysisResult`, `MethodDetailContext`) |
| `JBU.CodeLens.Core/Parsing/` | `ILanguageParser`, Roslyn C# parser, libclang C++ parser |
| `JBU.CodeLens.Core/Analysis/` | `ScideEngine` (scan orchestration), deterministic inference, relationships/call graph/metrics, category classification |
| `JBU.CodeLens.Core/AI/` | `ExplanationService`, conversation sessions, model path resolution |
| `JBU.CodeLens.Core/Export/` | Word/Markdown/JSON exporters behind `ExportService` |
| `JBU.CodeLens.UI` | WPF desktop GUI (views, renderers, theme, UI helpers) |

## Technology Stack

- **Runtime**: .NET 8.0
- **C# Parser**: Microsoft.CodeAnalysis.CSharp (Roslyn) 5.3.0
- **C++ Parser**: libclang via direct P/Invoke (`libclang.runtime.win-x64`)
- **LLM**: LLamaSharp, local GGUF model (Qwen2.5-Coder-1.5B-Instruct by default)
- **UI**: WPF
- **Testing**: xUnit, coverlet
- **Serialization**: System.Text.Json

## History

The structural layer (`Analysis/` relationship/graph/metrics builders plus the exporters) used
to be nine separate `SCIDE.*` projects that duplicated JBU.CodeLens's scanner and parsers and
carried a fully-disabled second LLM stack. They were merged into `JBU.CodeLens.Core` (duplicates
deleted — a project used to be parsed twice), and the 2026-07 restructure then dissolved the
merged `Structural/` folder into `Analysis/`, `Export/`, and the `JBU.CodeLens.Shared` DTO
assembly.

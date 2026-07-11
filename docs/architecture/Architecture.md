# CodeLensAI Architecture

## System Overview

```
┌─────────────────────────────────────────────────────────┐
│                CodeLensAI.UI (WPF)                       │
│    calls ScideEngine.AnalyzeProject once per scan         │
└────────────────────┬────────────────────────────────────┘
                     │
                     ▼
┌─────────────────────────────────────────────────────────┐
│              CodeLensAI.Core (single project)             │
│                                                           │
│  CSharpParser / CppParser ──► ClassInfo tree (ONE pass)   │
│         │                         │                       │
│         │                         ├──► UI file/class tree │
│         │                         │                       │
│         ▼                         ▼                       │
│  Analysis/ (deterministic   Structural/TypeInfoConverter   │
│  per-method inference:            │                       │
│  pre/post-conditions,             ▼                       │
│  execution flow, risks)     Structural/ScideEngine         │
│                              (SymbolTable, RelationshipExtractor,│
│                               CallGraphBuilder, MetricsCalculator,│
│                               KnowledgeGraph, Markdown/JsonExporter)│
└─────────────────────────────────────────────────────────┘
```

Everything under `src/CodeLensAI.Core/Structural/` used to be nine separate `SCIDE.*` projects
(`SCIDE.Core`, `SCIDE.Scanner`, `SCIDE.Parser.CSharp`, `SCIDE.Parser.Cpp`, `SCIDE.Analysis`,
`SCIDE.Graph`, `SCIDE.LLM`, `SCIDE.Export`, `SCIDE.API`, `SCIDE.Inference`) that had drifted into
duplicating `CodeLensAI.Core`'s own scanner/parsers, plus a fully-disabled second LLM stack. They
were merged into `CodeLensAI.Core` as a single project: the genuinely duplicate scanner and parsers
were deleted (`CodeLensAI.Core`'s own `CSharpParser`/`CppParser`/`DirectoryScanner` are now the only
parse path — a project used to be parsed twice, once for the UI tree and once for structural
analysis), and the dead `SCIDE.LLM` provider stack was removed outright. The additive parts —
project-wide relationships, call graph, metrics, knowledge graph, and Markdown/JSON export — moved
into `CodeLensAI.Core/Structural/` largely as-is.

## Pipeline

1. **Parse (once)** — `ScideEngine.AnalyzeProject` walks the project with `DirectoryScanner`, then
   parses every file with `CSharpParser` (Roslyn) or `CppParser` (libclang via ClangSharp). This is
   the same parse pass that produces the `ClassInfo` tree backing the UI's file/class tree — nothing
   gets parsed a second time.
2. **Deterministic per-method inference** — `CodeLensAI.Core.Analysis.InferenceEngine` runs against
   each parsed method (preconditions, postconditions, execution steps, design constraints, runtime
   risks), same as before.
3. **Structural conversion** — `Structural/TypeInfoConverter` converts each `ClassInfo` into the
   project-wide `TypeInfo` shape used by the structural-analysis layer.
4. **Analysis** — `SymbolTable`, `RelationshipExtractor` (INHERITS/IMPLEMENTS/CALLS edges),
   `CallGraphBuilder`, and `MetricsCalculator` (cyclomatic complexity, coupling, inheritance depth,
   maintainability index) run over the converted `ProjectIR`.
5. **Graph** — `KnowledgeGraph.BuildFrom` constructs typed nodes (namespace, class) and typed edges
   (CONTAINS, INHERITS, IMPLEMENTS, CALLS).
6. **Export** — `MarkdownExporter` and `JsonExporter` serialize `ProjectIR` data; `InferenceExportHelper`
   layers the deterministic per-method analysis on top.

There is no LLM step in this pipeline. AI explanations run exclusively through the single
`ExplanationService` (LLamaSharp, local GGUF model) owned by `CodeLensAI.UI` — see
[LLM.md](LLM.md) for why a second LLM path was removed rather than kept as an option.

## Module Responsibilities

| Location | Responsibility |
|---|---|
| `CodeLensAI.Core` (root) | Parsers (`CSharpParser`, `CppParser`), `DirectoryScanner`, `ClassInfo`/`MethodInfo` model, `ExplanationService`, `WordExporter` |
| `CodeLensAI.Core/Analysis/` | Deterministic per-method inference (pre/post-conditions, execution flow, design constraints, runtime risks) |
| `CodeLensAI.Core/Structural/` | Project-wide IR (`ProjectIR`/`TypeInfo`), `SymbolTable`, `RelationshipExtractor`, `CallGraphBuilder`, `MetricsCalculator`, `KnowledgeGraph`, `MarkdownExporter`/`JsonExporter`, `ScideEngine` (single entry point) |
| `CodeLensAI.UI` | WPF desktop GUI (the shipping app) |

## Technology Stack

- **Runtime**: .NET 8.0
- **C# Parser**: Microsoft.CodeAnalysis.CSharp (Roslyn) 5.3.0
- **C++ Parser**: libclang via ClangSharp
- **LLM**: LLamaSharp, local GGUF model (Qwen2.5-Coder-1.5B-Instruct by default)
- **UI**: WPF
- **Testing**: xUnit, coverlet
- **Serialization**: System.Text.Json

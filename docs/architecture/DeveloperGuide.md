# Developer Guide

## Prerequisites

- .NET 8.0 SDK
- Visual Studio 2022 (recommended) or any C# editor
- A local GGUF model file in `models/` for AI explanations (see [LLM.md](LLM.md))

## Build

```bash
dotnet restore
dotnet build
dotnet test
```

## Run

```bash
dotnet run --project src/JBU.CodeLens.UI
```

## Project Structure

```
JBU.CodeLens/
├── src/
│   ├── JBU.CodeLens.Core/         # Parsers, scanner, ExplanationService (the shipping engine)
│   │   ├── Analysis/            # Deterministic per-method inference
│   │   └── Structural/          # Project-wide IR, relationships, call graph, metrics,
│   │                            # knowledge graph, Markdown/JSON export, ScideEngine
│   └── JBU.CodeLens.UI/           # WPF desktop GUI (the shipping app)
├── tests/
│   └── JBU.CodeLens.Core.Tests/   # xUnit tests
├── docs/                        # Documentation
└── models/                      # Local GGUF model file (gitignored)
```

## Adding a New Language Parser

1. Implement `ILanguageParser` in `JBU.CodeLens.Core` (see `CSharpParser`/`CppParser`)
2. Wire it into `ScideEngine.AnalyzeProject`'s per-file parse step (`src/JBU.CodeLens.Core/Structural/ScideEngine.cs`)
3. `Structural/TypeInfoConverter` needs no changes — it converts from `ClassInfo`, which any language parser already produces

## Code Conventions

- No regions; no comments unless explaining a non-obvious decision
- Async methods use `Async` suffix
- Public API methods document exceptions in `<exception>` XML doc
- Tests follow Arrange-Act-Assert pattern
- Use `var` when type is obvious
- Prefer `Primary Constructor` syntax where applicable

## Testing

```bash
# Run all tests
dotnet test

# Run specific test class
dotnet test --filter "GraphTests"
```

## Git Workflow

```
main          — stable, release-ready
develop       — active development
feature/*     — feature branches (e.g. feature/parser-java)
bugfix/*      — bug fixes
```

Commit messages should be meaningful (e.g. "Implement Roslyn parser" not "update").

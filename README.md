# JBU CodeLens

A Windows desktop application that reads a folder of C# or C++ source code and produces
documentation of it: a browsable view of the project, the source of each method beside the
analysis of it, the limits each method places on its inputs, an automatic description of what
each method does, and exports to Word, Markdown and JSON.

Everything runs on the local machine. There is no network call and no source code leaves the
computer — the optional plain-English explanations come from a language model that runs in-process.

## Requirements

- Windows 10 or 11, 64-bit
- .NET 8 SDK (to build; the published build needs no runtime installed)
- Optional: a GGUF model file in `models/` for AI explanations. Without it everything except
  those explanations works normally.

The model file is not in this repository — it is around 1 GB, which is far past what a source
repository should carry. Any `.gguf` file placed in `models/` is picked up automatically, but the
prompts were written and tested against **`qwen2.5-coder-1.5b-instruct-q4_k_m.gguf`** (Qwen2.5-Coder
1.5B Instruct, Q4_K_M quantisation), available from the `Qwen/Qwen2.5-Coder-1.5B-Instruct-GGUF`
repository on Hugging Face. A different model will still work; the length and tone of the generated
text may differ.

## Build and run

```
dotnet build
dotnet run --project src/JBU.CodeLens.UI
```

In Visual Studio, open `JBU.CodeLens.sln` and set **JBU.CodeLens.UI** as the startup project.

## Tests

```
dotnet test
```

## Deploy

```
.\scripts\deploy.ps1
```

Publishes a self-contained build to `publish\` — around 420 MB, needing no .NET install on the
target machine. Copy your `.gguf` model into `publish\models\` before distributing; the script
creates the folder and reminds you.

## Layout

| Path | Contains |
| --- | --- |
| `src/JBU.CodeLens.UI` | The WPF application |
| `src/JBU.CodeLens.Core` | Parsing, analysis, the local model, and the exporters |
| `src/JBU.CodeLens.Shared` | Contracts and the data passed between the other two |
| `tests/` | Test suite |
| `assets/` | Logo sources and the generated icon |
| `scripts/` | Deployment script |

The user interface depends only on `Shared`; the engine never references the user interface. That
separation is deliberate and is what lets the engine run headless under test.

## Code quality

Two independent static analysis engines run on every build — Microsoft's .NET analysers and
SonarSource's C# analysers — with warnings treated as errors. Every rule that is switched off is
switched off in `.editorconfig` with a written reason.

The design document and the code quality report are kept outside this repository.

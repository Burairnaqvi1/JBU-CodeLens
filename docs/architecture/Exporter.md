# Export Module

## MarkdownExporter

**File:** `src/JBU.CodeLens.Core/Structural/MarkdownExporter.cs`

Exports `ProjectIR` to a Markdown document with:

- Project title and metrics overview table
- Namespace hierarchy with headings
- Class/interface/struct/enum details
- Method signatures, parameters, return types
- Properties and fields
- XML doc comments
- Metrics summary (classes, methods, complexity, MI)

## JsonExporter

**File:** `src/JBU.CodeLens.Core/Structural/JsonExporter.cs`

Exports `ProjectIR` as a JSON object using `System.Text.Json`:

- Full `ProjectIR` structure serialized to JSON
- Includes all namespaces, types, methods, properties, fields, relationships
- Pretty-printed, camelCase property names

## InferenceExportHelper

**File:** `src/JBU.CodeLens.Core/Structural/InferenceExportHelper.cs`

Wraps both exporters and appends deterministic per-method inference results
(`MethodAnalysis` cached on each method during the scan). This is what the UI's
MD/JSON export buttons call.

## Usage

```csharp
var markdown = new MarkdownExporter().Export(projectIR);
File.WriteAllText("docs/api.md", markdown);

var json = new JsonExporter().Export(projectIR);
File.WriteAllText("docs/api.json", json);

// Or, including per-method analysis (what the UI uses):
InferenceExportHelper.WriteMarkdownFile(projectIR, parseResults, "docs/api.md");
InferenceExportHelper.WriteJsonFile(projectIR, parseResults, "docs/api.json");
```

## Adding a New Format

1. Create a class implementing your format logic (no interface needed — export is simple)
2. Wire it into `InferenceExportHelper` if it should include per-method analysis
3. Add an export button/handler in `MainWindow` that calls it off the UI thread

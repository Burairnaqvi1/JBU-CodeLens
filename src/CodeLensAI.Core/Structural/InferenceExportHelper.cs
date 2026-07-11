using System.Text;
using System.Text.Json;
using CodeLensAI.Core;
using CodeLensAI.Core.Analysis;

namespace CodeLensAI.Core.Structural;

/// <summary>
/// Extends the structural Markdown/JSON export with deterministic per-method inference results.
/// </summary>
public static class InferenceExportHelper
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string BuildMarkdown(ProjectIR ir, IReadOnlyList<ParseResult> parseResults)
    {
        var baseMd = new MarkdownExporter().Export(ir);
        var sb = new StringBuilder(baseMd);
        sb.AppendLine();
        sb.AppendLine("## Deterministic Method Analysis");
        sb.AppendLine();

        foreach (var parseResult in parseResults)
        {
            foreach (var classInfo in parseResult.Classes)
            {
                foreach (var method in classInfo.Methods)
                {
                    if (method.CachedAnalysis is not { } analysis)
                    {
                        continue;
                    }

                    sb.AppendLine($"### {classInfo.Name}.{method.Name}");
                    sb.AppendLine();
                    AppendAnalysisMarkdown(sb, analysis);
                    sb.AppendLine();
                }
            }
        }

        return sb.ToString();
    }

    public static string BuildJson(ProjectIR ir, IReadOnlyList<ParseResult> parseResults)
    {
        var baseJson = new JsonExporter().Export(ir);
        using var doc = JsonDocument.Parse(baseJson);
        var root = new Dictionary<string, object?>();

        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            root[prop.Name] = JsonSerializer.Deserialize<object>(prop.Value.GetRawText());
        }

        var methodAnalyses = new List<Dictionary<string, object?>>();
        foreach (var parseResult in parseResults)
        {
            foreach (var classInfo in parseResult.Classes)
            {
                foreach (var method in classInfo.Methods)
                {
                    if (method.CachedAnalysis is not { } analysis)
                    {
                        continue;
                    }

                    methodAnalyses.Add(new Dictionary<string, object?>
                    {
                        ["className"] = classInfo.Name,
                        ["methodName"] = method.Name,
                        ["filePath"] = parseResult.FilePath,
                        ["preconditions"] = analysis.Preconditions.Select(p => p.Description).ToList(),
                        ["postconditions"] = analysis.Postconditions.Select(p => p.Description).ToList(),
                        ["stateChanges"] = analysis.StateChanges.Select(s => s.Description).ToList(),
                        ["executionSteps"] = analysis.ExecutionSteps
                            .Select(s => new Dictionary<string, object> { ["stepNumber"] = s.StepNumber, ["description"] = s.Description })
                            .ToList(),
                        ["designConstraints"] = analysis.DesignConstraints.Select(c => c.Description).ToList(),
                        ["runtimeRisks"] = analysis.RuntimeRisks.Select(r => r.Description).ToList(),
                    });
                }
            }
        }

        root["methodAnalyses"] = methodAnalyses;
        return JsonSerializer.Serialize(root, JsonOptions);
    }

    public static void WriteMarkdownFile(ProjectIR ir, IReadOnlyList<ParseResult> parseResults, string path) =>
        File.WriteAllText(path, BuildMarkdown(ir, parseResults));

    public static void WriteJsonFile(ProjectIR ir, IReadOnlyList<ParseResult> parseResults, string path) =>
        File.WriteAllText(path, BuildJson(ir, parseResults));

    private static void AppendAnalysisMarkdown(StringBuilder sb, MethodAnalysis analysis)
    {
        sb.AppendLine("**Preconditions**");
        if (analysis.Preconditions.Count == 0)
        {
            sb.AppendLine("- _(none detected)_");
        }
        else
        {
            foreach (var item in analysis.Preconditions)
            {
                sb.AppendLine($"- {item.Description}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("**Postconditions**");
        if (analysis.Postconditions.Count == 0 && analysis.StateChanges.Count == 0)
        {
            sb.AppendLine("- _(none detected)_");
        }
        else
        {
            foreach (var item in analysis.Postconditions)
            {
                sb.AppendLine($"- {item.Description}");
            }

            foreach (var item in analysis.StateChanges)
            {
                sb.AppendLine($"- {item.Description}");
            }
        }

        if (analysis.ExecutionSteps.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("**Execution flow**");
            foreach (var step in analysis.ExecutionSteps)
            {
                sb.AppendLine($"{step.StepNumber}. {step.Description}");
            }
        }

        if (analysis.DesignConstraints.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("**Design constraints**");
            foreach (var item in analysis.DesignConstraints)
            {
                sb.AppendLine($"- {item.Description}");
            }
        }
    }
}

using System.Text.Json;

using JBU.CodeLens.Shared.Structural;

namespace JBU.CodeLens.Core.Export;

public static class JsonExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string Export(ProjectIR ir)
    {
        ArgumentNullException.ThrowIfNull(ir);

        var data = new Dictionary<string, object?>
        {
            ["projectName"] = ir.ProjectName,
            ["rootPath"] = ir.RootPath,
            ["filesAnalyzed"] = ir.FilesAnalyzed,
            ["filesFailed"] = ir.FilesFailed,
            ["metrics"] = ir.Metrics != null ? new Dictionary<string, object>
            {
                ["totalClasses"] = ir.Metrics.TotalClasses,
                ["totalMethods"] = ir.Metrics.TotalMethods,
                ["totalProperties"] = ir.Metrics.TotalProperties,
                ["totalFields"] = ir.Metrics.TotalFields,
                ["totalNamespaces"] = ir.Metrics.TotalNamespaces,
                ["totalRelationships"] = ir.Metrics.TotalRelationships,
                ["averageComplexity"] = ir.Metrics.AverageComplexity,
                ["maxComplexity"] = ir.Metrics.MaxComplexity,
                ["maxInheritanceDepth"] = ir.Metrics.MaxInheritanceDepth,
                ["averageCoupling"] = ir.Metrics.AverageCoupling,
                ["maintainabilityIndex"] = ir.Metrics.MaintainabilityIndex,
            } : null,
            ["namespaces"] = ir.Namespaces.Select(ns => new Dictionary<string, object>
            {
                ["name"] = ns.Name,
                ["classes"] = ns.Classes.Select(c => c.FullName).ToList(),
            }).ToList(),
            ["relationships"] = ir.Relationships.Select(r => new Dictionary<string, object>
            {
                ["sourceId"] = r.SourceId,
                ["targetId"] = r.TargetId,
                ["kind"] = r.Kind,
                ["sourceFile"] = r.SourceFile,
            }).ToList(),
        };

        return JsonSerializer.Serialize(data, JsonOptions);
    }

    /// <summary>
    /// Writes the export, replacing any existing file only once the new one is complete.
    /// </summary>
    /// <remarks>
    /// Written through the same temp-file-then-move used by every other export. Writing directly
    /// meant an interruption part-way left a truncated file in place of the previous good one,
    /// and a half-written JSON file is worse than none: it parses far enough to look real.
    /// </remarks>
    public static void ExportToFile(ProjectIR ir, string outputPath) =>
        AtomicFileWriter.Write(outputPath, temp => File.WriteAllText(temp, Export(ir)));
}

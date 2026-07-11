using System.Text.Json;

using CodeLensAI.Shared.Structural;

namespace CodeLensAI.Core.Export;

public class JsonExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public string Export(ProjectIR ir)
    {
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

    public void ExportToFile(ProjectIR ir, string outputPath) => File.WriteAllText(outputPath, Export(ir));
}

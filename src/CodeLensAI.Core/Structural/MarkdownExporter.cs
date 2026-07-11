using System.Text;

namespace CodeLensAI.Core.Structural;

public class MarkdownExporter
{
    public string Export(ProjectIR ir)
    {
        var sb = new StringBuilder();
        var m = ir.Metrics;

        sb.AppendLine($"# Project: {ir.ProjectName}");
        sb.AppendLine();

        sb.AppendLine("## Overview");
        sb.AppendLine();
        sb.AppendLine("| Metric | Value |");
        sb.AppendLine("|--------|-------|");
        sb.AppendLine($"| Files | {ir.FilesAnalyzed} |");
        sb.AppendLine($"| Classes | {m?.TotalClasses ?? 0} |");
        sb.AppendLine($"| Methods | {m?.TotalMethods ?? 0} |");
        sb.AppendLine($"| Properties | {m?.TotalProperties ?? 0} |");
        sb.AppendLine($"| Namespaces | {m?.TotalNamespaces ?? 0} |");
        sb.AppendLine($"| Relationships | {m?.TotalRelationships ?? 0} |");
        sb.AppendLine($"| Avg Complexity | {m?.AverageComplexity:F2} |");
        sb.AppendLine($"| Maintainability | {m?.MaintainabilityIndex:F0} |");
        sb.AppendLine();

        sb.AppendLine("## Namespaces");
        sb.AppendLine();
        foreach (var ns in ir.Namespaces)
        {
            sb.AppendLine($"### {ns.Name}");
            sb.AppendLine();
            if (ns.Classes.Count > 0)
            {
                sb.AppendLine("**Classes:**");
                foreach (var cls in ns.Classes)
                    sb.AppendLine($"- `{cls.Name}`");
                sb.AppendLine();
            }
        }

        sb.AppendLine("## Classes");
        sb.AppendLine();
        foreach (var cls in ir.Classes)
        {
            sb.AppendLine($"### {cls.FullName}");
            sb.AppendLine();
            sb.AppendLine($"- **Kind:** {cls.Kind}");
            sb.AppendLine($"- **Access:** {cls.AccessModifier}");
            if (cls.BaseTypes.Count > 0)
                sb.AppendLine($"- **Base types:** {string.Join(", ", cls.BaseTypes)}");
            if (cls.ImplementedInterfaces.Count > 0)
                sb.AppendLine($"- **Interfaces:** {string.Join(", ", cls.ImplementedInterfaces)}");
            sb.AppendLine($"- **File:** `{cls.FilePath}`");
            sb.AppendLine();

            if (cls.Methods.Count > 0)
            {
                sb.AppendLine("#### Methods");
                sb.AppendLine();
                sb.AppendLine("| Name | Return | Params | Complexity |");
                sb.AppendLine("|------|--------|--------|------------|");
                foreach (var method in cls.Methods)
                {
                    var p = string.Join(", ", method.Parameters.Select(p => $"{p.TypeName} {p.Name}"));
                    sb.AppendLine($"| `{method.Name}` | `{method.ReturnType}` | {p} | {method.CyclomaticComplexity} |");
                }
                sb.AppendLine();
            }

            if (cls.Properties.Count > 0)
            {
                sb.AppendLine("#### Properties");
                sb.AppendLine();
                foreach (var prop in cls.Properties)
                    sb.AppendLine($"- `{prop.TypeName} {prop.Name}`");
                sb.AppendLine();
            }
        }

        if (ir.Relationships.Count > 0)
        {
            sb.AppendLine("## Relationships");
            sb.AppendLine();
            sb.AppendLine("| Source | Kind | Target |");
            sb.AppendLine("|--------|------|--------|");
            foreach (var rel in ir.Relationships)
                sb.AppendLine($"| {rel.SourceId} | {rel.Kind} | {rel.TargetId} |");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public void ExportToFile(ProjectIR ir, string outputPath) => File.WriteAllText(outputPath, Export(ir));
}

using System.Globalization;
using System.Text;

using JBU.CodeLens.Shared.Structural;

namespace JBU.CodeLens.Core.Export;

public static class MarkdownExporter
{
    public static string Export(ProjectIR ir)
    {
        ArgumentNullException.ThrowIfNull(ir);

        var sb = new StringBuilder();
        var m = ir.Metrics;

        sb.AppendLine(CultureInfo.InvariantCulture, $"# Project: {ir.ProjectName}");
        sb.AppendLine();

        sb.AppendLine("## Overview");
        sb.AppendLine();
        sb.AppendLine("| Metric | Value |");
        sb.AppendLine("|--------|-------|");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Files | {ir.FilesAnalyzed} |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Classes | {m?.TotalClasses ?? 0} |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Methods | {m?.TotalMethods ?? 0} |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Properties | {m?.TotalProperties ?? 0} |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Namespaces | {m?.TotalNamespaces ?? 0} |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Relationships | {m?.TotalRelationships ?? 0} |");

        sb.AppendLine();

        sb.AppendLine("## Namespaces");
        sb.AppendLine();
        foreach (var ns in ir.Namespaces)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"### {ns.Name}");
            sb.AppendLine();
            if (ns.Classes.Count > 0)
            {
                sb.AppendLine("**Classes:**");
                foreach (var cls in ns.Classes)
                    sb.AppendLine(CultureInfo.InvariantCulture, $"- `{cls.Name}`");
                sb.AppendLine();
            }
        }

        sb.AppendLine("## Classes");
        sb.AppendLine();
        foreach (var cls in ir.Classes)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"### {cls.FullName}");
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"- **Kind:** {cls.Kind}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"- **Access:** {cls.AccessModifier}");
            if (cls.BaseTypes.Count > 0)
                sb.AppendLine(CultureInfo.InvariantCulture, $"- **Base types:** {string.Join(", ", cls.BaseTypes)}");
            if (cls.ImplementedInterfaces.Count > 0)
                sb.AppendLine(CultureInfo.InvariantCulture, $"- **Interfaces:** {string.Join(", ", cls.ImplementedInterfaces)}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"- **File:** `{cls.FilePath}`");
            sb.AppendLine();

            if (cls.Methods.Count > 0)
            {
                sb.AppendLine("#### Methods");
                sb.AppendLine();
                sb.AppendLine("| Name | Return | Params |");
                sb.AppendLine("|------|--------|--------|");
                foreach (var method in cls.Methods)
                {
                    var p = string.Join(", ", method.Parameters.Select(p => $"{p.TypeName} {p.Name}"));
                    sb.AppendLine(CultureInfo.InvariantCulture, $"| `{method.Name}` | `{method.ReturnType}` | {p} |");
                }
                sb.AppendLine();
            }

            if (cls.Properties.Count > 0)
            {
                sb.AppendLine("#### Properties");
                sb.AppendLine();
                foreach (var prop in cls.Properties)
                    sb.AppendLine(CultureInfo.InvariantCulture, $"- `{prop.TypeName} {prop.Name}`");
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
                sb.AppendLine(CultureInfo.InvariantCulture, $"| {rel.SourceId} | {rel.Kind} | {rel.TargetId} |");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public static void ExportToFile(ProjectIR ir, string outputPath) => File.WriteAllText(outputPath, Export(ir));
}

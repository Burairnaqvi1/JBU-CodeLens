using System.Text.Json;

using CodeLensAI.Core.Export;
using CodeLensAI.Shared.Structural;

namespace CodeLensAI.Core.Tests;

/// <summary>
/// The JSON export is consumed by external tooling, so it must stay well-formed no matter
/// what characters appear in project names, paths, or type names.
/// </summary>
public class JsonExporterTests
{
    [Fact]
    public void Export_SpecialCharactersInNames_ProducesValidJsonThatRoundTrips()
    {
        var ir = new ProjectIR
        {
            ProjectName = "проект \"quoted\" <tag> & C:\\path\\日本語",
            RootPath = "C:\\Users\\admin\\проект",
            FilesAnalyzed = 3,
        };
        ir.Namespaces.Add(new NamespaceInfo
        {
            Name = "App.Ünïcode",
            Classes = { new TypeInfo { FullName = "App.Ünïcode.Wid\"get" } },
        });
        ir.Relationships.Add(new Relationship
        {
            SourceId = "a\\b",
            TargetId = "c\"d",
            Kind = "inherits",
            SourceFile = "C:\\src\\file.cs",
        });

        var json = new JsonExporter().Export(ir);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(ir.ProjectName, root.GetProperty("projectName").GetString());
        Assert.Equal(3, root.GetProperty("filesAnalyzed").GetInt32());
        Assert.Equal("App.Ünïcode", root.GetProperty("namespaces")[0].GetProperty("name").GetString());
        Assert.Equal("c\"d", root.GetProperty("relationships")[0].GetProperty("targetId").GetString());
    }

    [Fact]
    public void Export_EmptyProject_ProducesValidJsonWithNullMetrics()
    {
        var json = new JsonExporter().Export(new ProjectIR());

        using var document = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("metrics").ValueKind);
        Assert.Empty(document.RootElement.GetProperty("namespaces").EnumerateArray());
    }
}

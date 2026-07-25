using JBU.CodeLens.Core.Parsing.CSharp;
using JBU.CodeLens.Shared.Models;

namespace JBU.CodeLens.Core.Tests;

/// <summary>
/// Tests for the Roslyn-backed C# parser, focused on XML documentation extraction — the
/// summaries are shown verbatim in the UI and exports, so dropped inline tags leave visible
/// holes mid-sentence.
/// </summary>
public sealed class CSharpParserTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("codelens-cs-tests").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private ParseResult Parse(string content)
    {
        var path = Path.Combine(_tempDir, "test.cs");
        File.WriteAllText(path, content);
        return new CSharpParser().Parse(path);
    }

    [Fact]
    public void Summary_WithInlineCodeAndSeeCref_KeepsTheirText()
    {
        var result = Parse("""
            /// <summary>
            /// Uses the palettes in <c>Theme/Dark.xaml</c>, built by <see cref="ThemeManager"/>.
            /// </summary>
            public class Widget
            {
            }
            """);

        Assert.Empty(result.Errors);
        var widget = Assert.Single(result.Classes);
        Assert.Equal(
            "Uses the palettes in Theme/Dark.xaml, built by ThemeManager.",
            widget.XmlSummary);
    }

    [Fact]
    public void MethodSummary_WithParamrefAndNestedFormatting_KeepsTheirText()
    {
        var result = Parse("""
            public class Widget
            {
                /// <summary>
                /// Scales by <paramref name="factor"/> and walks the body <b>once</b>.
                /// </summary>
                public int Scale(int factor) => factor * 2;
            }
            """);

        var method = Assert.Single(Assert.Single(result.Classes).Methods);
        Assert.Equal("Scales by factor and walks the body once.", method.XmlSummary);
    }

    [Fact]
    public void Summary_MultiLine_IsCollapsedToSingleLine()
    {
        var result = Parse("""
            /// <summary>
            /// First line
            /// second line.
            /// </summary>
            public class Widget
            {
            }
            """);

        Assert.Equal("First line second line.", Assert.Single(result.Classes).XmlSummary);
    }

    // ---- Type discovery: modern C# codebases are full of records, structs, and interfaces.
    // A type the parser skips is invisible in the tree, the metrics, and every export.

    [Fact]
    public void Record_IsDiscoveredWithItsMethods()
    {
        var result = Parse("""
            namespace App;

            public record Invoice
            {
                public decimal ComputeTotal(decimal rate) => rate * 2m;
            }
            """);

        var record = Assert.Single(result.Classes);
        Assert.Equal("Invoice", record.Name);
        Assert.Equal("App", record.NamespaceName);
        Assert.Equal("ComputeTotal", Assert.Single(record.Methods).Name);
    }

    [Fact]
    public void PositionalRecord_ParametersBecomeProperties()
    {
        var result = Parse("""
            namespace App;

            public record Point(int X, int Y);
            """);

        var record = Assert.Single(result.Classes);
        Assert.Equal(new[] { "X", "Y" }, record.Properties.Select(p => p.Name));
        Assert.All(record.Properties, p => Assert.Equal("int", p.Type));
    }

    [Fact]
    public void StructAndRecordStruct_AreDiscovered()
    {
        var result = Parse("""
            public struct Size
            {
                public int Area() => 0;
            }

            public readonly record struct Coordinate(double Lat, double Lon);
            """);

        Assert.Equal(new[] { "Size", "Coordinate" }, result.Classes.Select(c => c.Name));
    }

    [Fact]
    public void Interface_IsDiscoveredWithItsMethods()
    {
        var result = Parse("""
            namespace App.Contracts;

            public interface IExporter
            {
                void Export(string path);
                string Name { get; }
            }
            """);

        var contract = Assert.Single(result.Classes);
        Assert.Equal("IExporter", contract.Name);
        Assert.Equal("Export", Assert.Single(contract.Methods).Name);
        Assert.Equal("Name", Assert.Single(contract.Properties).Name);
    }

    [Fact]
    public void NestedNamespaceBlocks_ReportTheDottedNamespaceName()
    {
        var result = Parse("""
            namespace Outer
            {
                namespace Inner
                {
                    public class Deep { }
                }
            }
            """);

        var deep = Assert.Single(result.Classes);
        Assert.Equal("Deep", deep.Name);
        Assert.Equal("Outer.Inner", deep.NamespaceName);
    }

    [Fact]
    public void MixedTopLevelTypes_AreAllDiscovered()
    {
        var result = Parse("""
            namespace App;

            public interface IShape { }
            public record Circle(double Radius) : IShape;
            public struct Vector2 { }
            public class Renderer { }
            """);

        Assert.Equal(
            new[] { "IShape", "Circle", "Vector2", "Renderer" },
            result.Classes.Select(c => c.Name));
        Assert.Contains("IShape", result.Classes.Single(c => c.Name == "Circle").ImplementedInterfaces);
    }
}

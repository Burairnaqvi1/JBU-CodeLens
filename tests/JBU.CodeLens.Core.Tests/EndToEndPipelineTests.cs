using System.Text.Json;

using JBU.CodeLens.Core.Analysis;
using JBU.CodeLens.Core.Export;

using Xceed.Words.NET;

namespace JBU.CodeLens.Core.Tests;

/// <summary>
/// Drives the whole pipeline over real files on disk: scan, parse, deterministic inference,
/// structural conversion, relationships, call graph, metrics, knowledge graph, and all three
/// export formats.
/// </summary>
/// <remarks>
/// The other suites cover components in isolation. This one exists because the seams between them
/// were where a refactor could break the product without failing a single unit test, the exporters
/// becoming static classes, the analysers gaining argument guards, and every regex in the analysis
/// engine moving behind a timeout are all changes that compile cleanly and only misbehave when the
/// stages are run together on real input.
/// </remarks>
public sealed class EndToEndPipelineTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("codelens-e2e").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private async Task<string> WriteSourceTreeAsync()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "Shape.cs"), """
            namespace Geometry;

            /// <summary>A shape with an area.</summary>
            public abstract class Shape
            {
                protected double _scale = 1.0;

                public abstract double Area();

                public string Describe()
                {
                    return $"Shape with area {Area()}";
                }
            }
            """).ConfigureAwait(false);

        await File.WriteAllTextAsync(Path.Combine(_tempDir, "Circle.cs"), """
            namespace Geometry;

            public class Circle : Shape
            {
                private readonly double _radius;

                public Circle(double radius)
                {
                    if (radius <= 0)
                    {
                        throw new ArgumentOutOfRangeException(nameof(radius));
                    }

                    _radius = radius;
                }

                public override double Area()
                {
                    return 3.14159 * _radius * _radius;
                }

                public string Report(bool verbose)
                {
                    if (verbose)
                    {
                        for (var i = 0; i < 3; i++)
                        {
                            if (i % 2 == 0)
                            {
                                _scale += 0.1;
                            }
                        }
                    }

                    return Describe();
                }
            }
            """).ConfigureAwait(false);

        return _tempDir;
    }

    [Fact]
    public async Task FullPipeline_RealSourceTree_ProducesCoherentIrAndAllThreeExports()
    {
        var root = await WriteSourceTreeAsync();

        var result = await new ScideEngine().AnalyzeProjectAsync(root);

        // --- scan ---------------------------------------------------------------------------
        Assert.True(result.Success, result.Error);
        Assert.Empty(result.FailedFiles);
        Assert.Equal(2, result.AnalyzedFiles);

        var ir = result.Ir;
        Assert.NotNull(ir);

        // --- structural conversion ----------------------------------------------------------
        Assert.Contains(ir.Classes, c => c.Name == "Shape");
        Assert.Contains(ir.Classes, c => c.Name == "Circle");
        Assert.Contains(ir.Methods, m => m.Name == "Area");
        Assert.Contains(ir.Methods, m => m.Name == "Report");

        // --- relationships: Circle derives from Shape ----------------------------------------
        Assert.Contains(
            ir.Relationships,
            r => r.Kind == "INHERITS" && r.SourceId.Contains("Circle", StringComparison.Ordinal));

        // --- metrics --------------------------------------------------------------------------
        var metrics = result.Metrics;
        Assert.NotNull(metrics);
        Assert.Equal(2, metrics.TotalClasses);
        Assert.True(metrics.TotalMethods >= 4, $"Expected at least 4 methods, got {metrics.TotalMethods}.");
        Assert.True(metrics.TotalRelationships > 0, "Circle : Shape should produce at least one relationship.");
        Assert.Equal(
            Math.Round((double)metrics.TotalMethods / metrics.TotalClasses, 2),
            metrics.AverageMethodsPerClass);

        // --- knowledge graph -------------------------------------------------------------------
        Assert.NotNull(result.Graph);
        Assert.NotEmpty(result.Graph.Nodes);
        Assert.NotEmpty(result.Graph.Edges);

        // --- exports ---------------------------------------------------------------------------
        var exportService = new ExportService();

        var mdPath = Path.Combine(_tempDir, "out.md");
        exportService.ExportMarkdown(ir, result.ParseResults, mdPath);
        var markdown = await File.ReadAllTextAsync(mdPath);
        Assert.Contains("Circle", markdown, StringComparison.Ordinal);
        Assert.Contains("Shape", markdown, StringComparison.Ordinal);

        var jsonPath = Path.Combine(_tempDir, "out.json");
        exportService.ExportJson(ir, result.ParseResults, jsonPath);
        using var parsed = JsonDocument.Parse(await File.ReadAllTextAsync(jsonPath));
        Assert.True(parsed.RootElement.TryGetProperty("metrics", out _), "JSON export is missing 'metrics'.");

        var docxPath = Path.Combine(_tempDir, "out.docx");
        // No model in a test run: the AI sections are skipped and the deterministic ones must
        // still produce a complete document.
        exportService.ExportWord(
            docxPath,
            root,
            result.ParseResults,
            explanationService: null,
            includeAi: false,
            onProgress: null);
        using var document = DocX.Load(docxPath);
        Assert.Contains(document.Paragraphs, p => p.Text.Contains("Circle", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FullPipeline_RescanOfUnchangedTree_ReturnsIdenticalStructuralResults()
    {
        var root = await WriteSourceTreeAsync();
        var engine = new ScideEngine();

        var first = await engine.AnalyzeProjectAsync(root);
        var second = await engine.AnalyzeProjectAsync(root);

        // The second scan is served from the (path, last-write-time) cache. Serving from cache must
        // not change what the caller sees, which is the whole premise of the optimisation.
        Assert.True(second.Success, second.Error);
        Assert.Equal(first.ClassCount, second.ClassCount);
        Assert.Equal(first.MethodCount, second.MethodCount);
        Assert.Equal(first.Ir!.Relationships.Count, second.Ir!.Relationships.Count);
        Assert.Equal(first.Metrics!.TotalClasses, second.Metrics!.TotalClasses);
        Assert.Equal(first.Metrics.TotalRelationships, second.Metrics.TotalRelationships);
        Assert.Equal(first.Metrics.AverageMethodsPerClass, second.Metrics.AverageMethodsPerClass);
    }

    [Fact]
    public async Task FullPipeline_EveryMethodGetsDeterministicAnalysisAndKnowsItsClass()
    {
        var root = await WriteSourceTreeAsync();

        var result = await new ScideEngine().AnalyzeProjectAsync(root);

        Assert.True(result.Success, result.Error);

        var methods = result.ParseResults
            .SelectMany(p => p.Classes)
            .SelectMany(c => c.Methods)
            .ToList();

        Assert.NotEmpty(methods);

        // The analysis runs across cores before the sequential assembly pass. If a method were
        // missed, or its declaring class were not attached before analysis ran, the detail panel
        // and every export would silently lose that method's preconditions and execution flow.
        Assert.All(methods, m =>
        {
            Assert.NotNull(m.ParentClass);
            Assert.NotNull(m.CachedAnalysis);
        });
    }

    [Fact]
    public async Task FullPipeline_UnparseableFile_IsReportedWithoutFailingTheScan()
    {
        await WriteSourceTreeAsync();

        // Deliberately malformed: the resilience design says one bad file degrades to a reported
        // error, never a failed scan or a crash.
        await File.WriteAllTextAsync(
            Path.Combine(_tempDir, "Broken.cs"),
            "namespace Broken { public class Unclosed { public void M( { ");

        var result = await new ScideEngine().AnalyzeProjectAsync(_tempDir);

        Assert.True(result.Success, result.Error);
        Assert.Contains(result.Ir!.Classes, c => c.Name == "Circle");
    }
}

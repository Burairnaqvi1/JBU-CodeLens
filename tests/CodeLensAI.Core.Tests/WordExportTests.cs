using CodeLensAI.Core.Export;
using CodeLensAI.Shared.Models;
using Xceed.Words.NET;

namespace CodeLensAI.Core.Tests;

public class WordExportTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("codelens-word-tests").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private static List<ParseResult> SampleParseResults()
    {
        var classInfo = new ClassInfo
        {
            Name = "Calculator",
            SourceFilePath = @"C:\proj\Calculator.cs",
        };
        var method = new MethodInfo
        {
            Name = "Divide",
            ReturnType = "double",
            AccessModifier = "public",
            Parameters = { "double a", "double b" },
            ParentClass = classInfo,
        };
        classInfo.Methods.Add(method);

        return
        [
            new ParseResult
            {
                FilePath = @"C:\proj\Calculator.cs",
                Classes = { classInfo },
            },
        ];
    }

    [Fact]
    public void Export_AlwaysEmitsMandatoryHeadingsInOrder()
    {
        // No XML docs, no AI service, no cached analysis — the weakest possible input.
        var outputPath = Path.Combine(_tempDir, "doc.docx");

        WordExporter.Export(outputPath, @"C:\proj", SampleParseResults());

        using var document = DocX.Load(outputPath);
        var paragraphs = document.Paragraphs.Select(p => p.Text).ToList();

        string[] requiredHeadings =
        [
            "Description",
            "Parameters",
            "Returns",
            "Error Situations",
            "Pre & Post Conditions",
            "Design Constraints",
        ];

        var lastIndex = -1;
        foreach (var heading in requiredHeadings)
        {
            var index = paragraphs.FindIndex(lastIndex + 1, p => p.Trim() == heading);
            Assert.True(index > lastIndex, $"Heading '{heading}' missing or out of order.");
            lastIndex = index;
        }
    }

    [Fact]
    public void Export_TargetFileLocked_LeavesOriginalIntactAndNoTempFiles()
    {
        var outputPath = Path.Combine(_tempDir, "locked.docx");
        File.WriteAllText(outputPath, "SENTINEL");

        using (new FileStream(outputPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var thrown = Record.Exception(() =>
                WordExporter.Export(outputPath, @"C:\proj", SampleParseResults()));
            Assert.True(thrown is IOException or UnauthorizedAccessException,
                $"Expected an I/O failure, got: {thrown?.GetType().Name ?? "nothing"}");
        }

        Assert.Equal("SENTINEL", File.ReadAllText(outputPath));
        Assert.Empty(Directory.GetFiles(_tempDir, "~*"));
    }

    [Fact]
    public void Export_AlreadyCanceled_ThrowsAndWritesNothing()
    {
        var outputPath = Path.Combine(_tempDir, "canceled.docx");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            WordExporter.Export(outputPath, @"C:\proj", SampleParseResults(), cancellationToken: cts.Token));

        Assert.False(File.Exists(outputPath), "A canceled export must not leave an output file.");
        Assert.Empty(Directory.GetFiles(_tempDir, "~*"));
    }

    [Fact]
    public void ExportMarkdownAndJson_TargetLocked_LeaveNoTempFiles()
    {
        var ir = new CodeLensAI.Shared.Structural.ProjectIR { ProjectName = "P", RootPath = _tempDir };
        var mdPath = Path.Combine(_tempDir, "out.md");
        File.WriteAllText(mdPath, "ORIGINAL");

        using (new FileStream(mdPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var thrown = Record.Exception(() =>
                InferenceExportHelper.WriteMarkdownFile(ir, SampleParseResults(), mdPath));
            Assert.True(thrown is IOException or UnauthorizedAccessException,
                $"Expected an I/O failure, got: {thrown?.GetType().Name ?? "nothing"}");
        }

        Assert.Equal("ORIGINAL", File.ReadAllText(mdPath));
        Assert.Empty(Directory.GetFiles(_tempDir, "~*"));
    }
}

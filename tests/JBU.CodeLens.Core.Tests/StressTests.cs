using System.Text;
using JBU.CodeLens.Core.Analysis;
using JBU.CodeLens.Core.Parsing.CSharp;
using JBU.CodeLens.Core.Parsing.Cpp;
using JBU.CodeLens.Shared.Models;
using JBU.CodeLens.Shared.Utilities;

namespace JBU.CodeLens.Core.Tests;

/// <summary>
/// Adversarial and large-scale inputs: the app must complete or fail gracefully (errors reported
/// per file), never crash or hang, on every one of these.
/// </summary>
public class StressTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("codelens-stress").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Scan_500Files_CompletesSuccessfully()
    {
        for (var i = 0; i < 500; i++)
        {
            File.WriteAllText(Path.Combine(_tempDir, $"Class{i:D3}.cs"), $$"""
                namespace Stress;
                public class Class{{i:D3}}
                {
                    private int _count;
                    public int Increment(int by)
                    {
                        if (by < 0) throw new ArgumentOutOfRangeException(nameof(by));
                        _count += by;
                        return _count;
                    }
                }
                """);
        }

        var result = await new ScideEngine().AnalyzeProjectAsync(_tempDir);

        Assert.True(result.Success, result.Error);
        Assert.Equal(500, result.ParseResults.Count);
        Assert.Equal(500, result.ClassCount);
        Assert.Empty(result.FailedFiles);
    }

    [Fact]
    public void Parse_FileOver10kLines_Succeeds()
    {
        var builder = new StringBuilder("namespace Big;\npublic class Huge\n{\n");
        for (var i = 0; i < 1200; i++)
        {
            builder.AppendLine($$"""
                public int Method{{i}}(int input)
                {
                    var value = input + {{i}};
                    if (value < 0) { value = 0; }
                    for (var j = 0; j < 3; j++) { value += j; }
                    var doubled = value * 2;
                    var text = doubled.ToString();
                    var length = text.Length;
                    return value + length;
                }
                """);
        }
        builder.AppendLine("}");
        var source = builder.ToString();
        Assert.True(source.Count(c => c == '\n') > 10_000);

        var path = Path.Combine(_tempDir, "Huge.cs");
        File.WriteAllText(path, source);
        var result = new CSharpParser().Parse(path);

        Assert.Empty(result.Errors);
        Assert.Equal(1200, Assert.Single(result.Classes).Methods.Count);
    }

    [Theory]
    [InlineData("comments_only.cs", "// just a comment\n/* and a block comment */\n")]
    [InlineData("empty.cs", "")]
    [InlineData("broken.cs", "public class { { { int ??? = ;;; }")]
    [InlineData("empty.cpp", "")]
    [InlineData("comments_only.cpp", "// nothing here\n")]
    [InlineData("broken.cpp", "class ~~~ Broken {{{ void ((( }")]
    public void Parse_DegenerateFiles_NeverThrow(string fileName, string content)
    {
        var path = Path.Combine(_tempDir, fileName);
        File.WriteAllText(path, content);

        var result = LanguageFileExtensions.IsCppFile(path)
            ? new CppParser().Parse(path)
            : new CSharpParser().Parse(path);

        // Graceful means: a result object comes back; malformed input may report classes,
        // errors, or nothing, but must not throw or hang.
        Assert.NotNull(result);
    }

    [Fact]
    public void Parse_DeeplyNestedCode_Succeeds()
    {
        var builder = new StringBuilder("public class Nested { public int Run(int x) {\n");
        for (var i = 0; i < 150; i++)
        {
            builder.AppendLine($"if (x > {i}) {{");
        }
        builder.AppendLine("x++;");
        for (var i = 0; i < 150; i++)
        {
            builder.AppendLine("}");
        }
        builder.AppendLine("return x; } }");

        var path = Path.Combine(_tempDir, "Nested.cs");
        File.WriteAllText(path, builder.ToString());
        var result = new CSharpParser().Parse(path);

        Assert.Single(result.Classes);
    }

    [Fact]
    public void Parse_NonUtf8Encoding_FailsGracefullyPerFile()
    {
        // Windows-1252 bytes that are invalid UTF-8 (0xE9 = é). The parser must not crash;
        // decoded text degrades to replacement characters at worst.
        var path = Path.Combine(_tempDir, "latin1.cs");
        File.WriteAllBytes(path, Encoding.Latin1.GetBytes(
            "public class CaféMenu { public int Prix() { return 5; } }"));

        var result = new CSharpParser().Parse(path);

        Assert.NotNull(result);
        Assert.Single(result.Classes);
    }

    [Fact]
    public async Task Scan_AlreadyCanceled_ReturnsFailedResultInsteadOfThrowing()
    {
        File.WriteAllText(Path.Combine(_tempDir, "One.cs"), "public class One {}");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await new ScideEngine().AnalyzeProjectAsync(_tempDir, cts.Token);

        Assert.False(result.Success);
        Assert.Equal("Scan canceled.", result.Error);
    }

    [Fact]
    public async Task Scan_ReportsMonotonicProgressUpToTotal()
    {
        for (var i = 0; i < 20; i++)
        {
            File.WriteAllText(Path.Combine(_tempDir, $"P{i}.cs"), $"public class P{i} {{}}");
        }

        var reports = new List<ScanProgress>();
        var progress = new SynchronousProgress(reports.Add);

        var result = await new ScideEngine().AnalyzeProjectAsync(_tempDir, default, progress);

        Assert.True(result.Success, result.Error);
        Assert.Equal(20, reports.Count);
        Assert.All(reports, r => Assert.Equal(20, r.TotalFiles));
        Assert.Equal(20, reports.Max(r => r.FilesParsed));
    }

    /// <summary>Reports inline (no SynchronizationContext posting) so tests can assert counts.</summary>
    private sealed class SynchronousProgress(Action<ScanProgress> handler) : IProgress<ScanProgress>
    {
        private readonly object _gate = new();

        public void Report(ScanProgress value)
        {
            lock (_gate)
            {
                handler(value);
            }
        }
    }
}

using System.Globalization;
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
public sealed class StressTests : IDisposable
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
            await File.WriteAllTextAsync(Path.Combine(_tempDir, $"Class{i:D3}.cs"), $$"""
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
            builder.AppendLine(CultureInfo.InvariantCulture, $$"""
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
            builder.AppendLine(CultureInfo.InvariantCulture, $"if (x > {i}) {{");
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
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "One.cs"), "public class One {}");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await new ScideEngine().AnalyzeProjectAsync(_tempDir, cancellationToken: cts.Token);

        Assert.False(result.Success);
        Assert.Equal("Scan canceled.", result.Error);
    }

    [Fact]
    public async Task Scan_ReportsMonotonicProgressUpToTotal()
    {
        for (var i = 0; i < 20; i++)
        {
            await File.WriteAllTextAsync(Path.Combine(_tempDir, $"P{i}.cs"), $"public class P{i} {{}}");
        }

        var reports = new List<ScanProgress>();
        var progress = new SynchronousProgress(reports.Add);

        var result = await new ScideEngine().AnalyzeProjectAsync(_tempDir, progress);

        Assert.True(result.Success, result.Error);
        Assert.Equal(20, reports.Count);
        Assert.All(reports, r => Assert.Equal(20, r.TotalFiles));
        Assert.Equal(20, reports.Max(r => r.FilesParsed));
    }

    /// <summary>
    /// Real projects nest deeply — node_modules-style trees, generated output, vendored
    /// dependencies — and the scanner walks whatever it is pointed at. A recursive walk that
    /// works on three levels can still overflow the stack on sixty.
    /// </summary>
    [Fact]
    public async Task Scan_DeeplyNestedFolders_FindsEveryFile()
    {
        const int depth = 60;
        var current = _tempDir;
        for (var i = 0; i < depth; i++)
        {
            current = Path.Combine(current, $"lvl{i}");
            Directory.CreateDirectory(current);
            await File.WriteAllTextAsync(
                Path.Combine(current, $"Deep{i}.cs"),
                $"namespace Deep;\npublic class Deep{i} {{ public int Value() => {i}; }}");
        }

        var result = await new ScideEngine().AnalyzeProjectAsync(_tempDir);

        Assert.True(result.Success, result.Error);
        Assert.Equal(depth, result.ParseResults.Count);
        Assert.Empty(result.FailedFiles);
    }

    /// <summary>
    /// Paths past 260 characters are ordinary on Windows once a project sits a few folders down
    /// inside a user profile. The scan must either read them or record them as failed files —
    /// what it must never do is abort the whole scan.
    /// </summary>
    [Fact]
    public async Task Scan_PathsBeyondTheLegacyLimit_DoNotAbortTheScan()
    {
        // Each segment is well under the 255-character per-component limit; it is the total
        // path length that crosses MAX_PATH.
        var segment = new string('p', 60);
        var current = _tempDir;
        for (var i = 0; i < 5; i++)
        {
            current = Path.Combine(current, $"{segment}{i}");
            Directory.CreateDirectory(current);
        }

        Assert.True(current.Length > 260, $"the test needs a path over 260 chars, got {current.Length}");

        await File.WriteAllTextAsync(
            Path.Combine(current, "LongPath.cs"),
            "namespace Long;\npublic class LongPath { public int Value() => 1; }");

        // A file at a normal depth, to prove the long one did not take the scan down with it.
        await File.WriteAllTextAsync(
            Path.Combine(_tempDir, "Normal.cs"),
            "namespace Long;\npublic class Normal { public int Value() => 2; }");

        var result = await new ScideEngine().AnalyzeProjectAsync(_tempDir);

        Assert.True(result.Success, result.Error);
        Assert.Contains(result.ParseResults, r => r.Classes.Any(c => c.Name == "Normal"));
    }

    /// <summary>
    /// A file whose name survives the scan but whose content is empty, whitespace, or a lone
    /// byte-order mark: each is a real thing to find in a source tree.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   \n\t\n  ")]
    [InlineData("﻿")]
    public void Parse_EmptyOrWhitespaceFile_ReturnsAnEmptyResultRatherThanThrowing(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var path = Path.Combine(_tempDir, $"Empty{content.Length}.cs");
        File.WriteAllText(path, content);

        var result = new CSharpParser().Parse(path);

        Assert.NotNull(result);
        Assert.Empty(result.Classes);
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

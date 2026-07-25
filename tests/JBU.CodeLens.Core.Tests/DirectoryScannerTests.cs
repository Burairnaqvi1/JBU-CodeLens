using JBU.CodeLens.Core.Utilities;

namespace JBU.CodeLens.Core.Tests;

/// <summary>
/// Tests for source-file discovery: the scanner must find every supported source file, skip
/// generated/tooling folders at any depth, and never descend into an excluded tree (so a huge
/// node_modules or .git costs nothing).
/// </summary>
public class DirectoryScannerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("codelens-scan-tests").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string Touch(string relativePath)
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "// test");
        return full;
    }

    [Fact]
    public void FindsSupportedSourceFiles_AcrossExtensions()
    {
        Touch("a.cs");
        Touch("sub/b.cpp");
        Touch("sub/deep/c.h");
        Touch("d.hpp");
        Touch("notes.txt");
        Touch("data.json");

        var found = DirectoryScanner.ScanForSourceFiles(_root)
            .Select(Path.GetFileName)
            .ToList();

        Assert.Equal(new[] { "a.cs", "b.cpp", "c.h", "d.hpp" }, found.OrderBy(f => f));
        Assert.DoesNotContain("notes.txt", found);
        Assert.DoesNotContain("data.json", found);
    }

    [Theory]
    [InlineData("bin")]
    [InlineData("obj")]
    [InlineData(".git")]
    [InlineData("node_modules")]
    [InlineData("build")]
    [InlineData(".vs")]
    [InlineData("__pycache__")]
    public void ExcludesFilesInsideGeneratedAndToolingFolders(string excluded)
    {
        Touch("keep.cs");
        Touch($"{excluded}/skip.cs");
        Touch($"nested/{excluded}/alsoskip.cs");

        var found = DirectoryScanner.ScanForSourceFiles(_root).Select(Path.GetFileName).ToList();

        Assert.Contains("keep.cs", found);
        Assert.DoesNotContain("skip.cs", found);
        Assert.DoesNotContain("alsoskip.cs", found);
    }

    [Fact]
    public void ReturnsEmpty_ForMissingOrBlankRoot()
    {
        Assert.Empty(DirectoryScanner.ScanForSourceFiles(Path.Combine(_root, "does-not-exist")));
        Assert.Empty(DirectoryScanner.ScanForSourceFiles(""));
        Assert.Empty(DirectoryScanner.ScanForSourceFiles("   "));
    }

    [Fact]
    public void Results_AreSortedOrdinalIgnoreCase()
    {
        Touch("zebra.cs");
        Touch("Alpha.cs");
        Touch("mango.cs");

        var found = DirectoryScanner.ScanForSourceFiles(_root);

        var expected = found.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        Assert.Equal(expected, found);
    }
}

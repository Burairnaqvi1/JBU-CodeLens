namespace JBU.CodeLens.Core.Utilities;

/// <summary>
/// Locates source files within a project directory tree, skipping folders that contain
/// generated output or third-party code (such as <c>bin</c>, <c>obj</c>, <c>.git</c>, and
/// <c>node_modules</c>) so that only hand-written sources are returned.
/// </summary>
public static class DirectoryScanner
{
    /// <summary>
    /// File extensions considered "source files" for the current scan.
    /// </summary>
    private static readonly string[] SourceExtensions = { ".cs", ".cpp", ".hpp", ".h" };

    /// <summary>
    /// Path segments that mark a folder as excluded. Any file whose path passes through one
    /// of these folders is filtered out.
    /// </summary>
    private static readonly string[] ExcludedFolders =
    {
        "bin", "obj", ".git", "node_modules", "build", "__pycache__", ".vs",
    };

    /// <summary>
    /// Recursively enumerates every <c>.cs</c>, <c>.cpp</c>, <c>.hpp</c>, and <c>.h</c> file under
    /// <paramref name="rootPath"/>, excluding generated and tooling folders (<c>bin</c>, <c>obj</c>,
    /// <c>.git</c>, <c>node_modules</c>, <c>build</c>, <c>__pycache__</c>, <c>.vs</c>).
    /// </summary>
    /// <param name="rootPath">The root directory to scan.</param>
    /// <returns>
    /// A list of full file paths for the matching source files. Returns an empty list if
    /// <paramref name="rootPath"/> is null/empty or does not exist.
    /// </returns>
    public static List<string> ScanForSourceFiles(string rootPath)
    {
        var results = new List<string>();
        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
        {
            return results;
        }

        // Iterative walk that PRUNES excluded folders instead of enumerating everything and
        // filtering afterward: an excluded tree (a large node_modules or .git, tens of thousands
        // of files) is never descended into, so its files are never read from disk at all. A
        // per-directory try/catch keeps one protected or vanished subfolder from aborting the
        // whole scan (the old IgnoreInaccessible behavior); reparse-point directories are skipped
        // so symlinks/junctions can't create cycles or lead outside the selected tree.
        var pending = new Stack<string>();
        pending.Push(rootPath);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();

            try
            {
                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    if (IsSourceFile(file))
                    {
                        results.Add(file);
                    }
                }

                foreach (var subDirectory in Directory.EnumerateDirectories(directory))
                {
                    var name = Path.GetFileName(subDirectory);
                    if (ExcludedFolders.Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // Reading attributes can fail on its own — the folder may have been removed
                    // since it was enumerated, or be readable as a name but not as an entry. That
                    // must cost only this folder: handled by the outer catch it would abandon every
                    // sibling still to come in this directory, quietly under-scanning the project
                    // with nothing to show it happened.
                    bool isReparsePoint;
                    try
                    {
                        isReparsePoint = (File.GetAttributes(subDirectory) & FileAttributes.ReparsePoint) != 0;
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                    {
                        continue;
                    }

                    if (isReparsePoint)
                    {
                        continue;
                    }

                    pending.Push(subDirectory);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // Skip a folder we cannot read; the rest of the scan continues.
            }
        }

        results.Sort(StringComparer.OrdinalIgnoreCase);
        return results;
    }

    /// <summary>
    /// Returns true when the file's extension is one of the supported source extensions.
    /// </summary>
    private static bool IsSourceFile(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        return SourceExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }
}

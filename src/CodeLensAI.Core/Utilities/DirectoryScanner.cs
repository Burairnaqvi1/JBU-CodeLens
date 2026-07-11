namespace CodeLensAI.Core.Utilities;

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
        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
        {
            return new List<string>();
        }

        // IgnoreInaccessible: one protected subfolder must not abort the whole scan.
        // Skipping reparse points keeps the walk inside the selected tree and immune to
        // symlink/junction cycles.
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        return Directory
            .EnumerateFiles(rootPath, "*.*", options)
            .Where(IsSourceFile)
            .Where(path => !IsInExcludedFolder(path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Returns true when the file's extension is one of the supported source extensions.
    /// </summary>
    private static bool IsSourceFile(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        return SourceExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns true when any directory segment of <paramref name="filePath"/> matches one of
    /// the excluded folder names, compared case-insensitively against whole path segments.
    /// </summary>
    private static bool IsInExcludedFolder(string filePath)
    {
        var segments = filePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        foreach (var segment in segments)
        {
            if (ExcludedFolders.Contains(segment, StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

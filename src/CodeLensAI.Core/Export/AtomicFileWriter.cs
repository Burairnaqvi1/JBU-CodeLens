namespace CodeLensAI.Core.Export;

/// <summary>
/// Writes export output to a temporary file in the destination directory, then atomically moves
/// it over the target path. A failure mid-generation leaves the previous file (if any) intact
/// and cleans up the temp file, so exports can never leave a partial or corrupt document behind.
/// </summary>
internal static class AtomicFileWriter
{
    /// <summary>
    /// Invokes <paramref name="writeTo"/> with a temp path next to <paramref name="outputPath"/>
    /// (same volume, so the final move is atomic), then replaces the target on success.
    /// </summary>
    public static void Write(string outputPath, Action<string> writeTo)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (string.IsNullOrEmpty(directory))
        {
            throw new IOException($"Cannot resolve an output directory for '{outputPath}'.");
        }

        var tempPath = Path.Combine(directory, $".{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            writeTo(tempPath);
            File.Move(tempPath, outputPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (IOException)
                {
                    // Cleanup only — the export outcome was already decided above.
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }
}

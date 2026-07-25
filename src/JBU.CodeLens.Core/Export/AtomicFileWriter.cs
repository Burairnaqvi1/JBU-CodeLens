namespace JBU.CodeLens.Core.Export;

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

        // The temp name keeps the target's real extension: some writers (DocX) silently append
        // their own extension when given anything else, which would break the final move.
        var tempPath = Path.Combine(
            directory,
            $"~{Path.GetFileNameWithoutExtension(outputPath)}.{Guid.NewGuid():N}{Path.GetExtension(outputPath)}");
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

namespace CodeLensAI.Core.AI;

/// <summary>
/// Locates a local GGUF model file for <see cref="ExplanationService"/>.
/// </summary>
public static class ModelPathResolver
{
    private const int MaxParentWalkDepth = 8;

    /// <summary>
    /// Searches deployment and development locations for a <c>.gguf</c> file.
    /// </summary>
    /// <returns>The first model file found, or <c>null</c> when none exists.</returns>
    public static string? Resolve()
    {
        var baseDir = AppContext.BaseDirectory;

        var found = TryFindGgufInDirectory(Path.Combine(baseDir, "models"));
        if (found is not null)
        {
            return found;
        }

        found = TryFindGgufInDirectory(baseDir);
        if (found is not null)
        {
            return found;
        }

        var current = baseDir;
        for (var depth = 0; depth < MaxParentWalkDepth; depth++)
        {
            current = Directory.GetParent(current)?.FullName ?? string.Empty;
            if (string.IsNullOrEmpty(current))
            {
                break;
            }

            found = TryFindGgufInDirectory(Path.Combine(current, "models"));
            if (found is not null)
            {
                return found;
            }
        }

        current = baseDir;
        for (var depth = 0; depth < MaxParentWalkDepth; depth++)
        {
            current = Directory.GetParent(current)?.FullName ?? string.Empty;
            if (string.IsNullOrEmpty(current))
            {
                break;
            }

            found = TryFindGgufInDirectory(current);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// User-facing guidance when no model file is present.
    /// </summary>
    public static string ModelNotFoundMessage => AiGuidance.ModelNotFoundMessage;

    private static string? TryFindGgufInDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }

        return Directory
            .EnumerateFiles(directory, "*.gguf", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }
}

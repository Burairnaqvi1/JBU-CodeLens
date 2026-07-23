using System.Text.Json;

namespace CodeLensAI.Core.AI;

/// <summary>
/// Locates a local GGUF model file for <see cref="ExplanationService"/>.
/// Resolution order: explicit configuration first — the <c>CODELENSAI_MODEL_PATH</c>
/// environment variable, then <c>modelPath</c> in <c>%APPDATA%\CodeLensAI\settings.json</c> —
/// and only then the automatic search of deployment/development locations. An explicit
/// configuration is authoritative: when set but invalid, resolution fails rather than silently
/// loading some other model found on disk.
/// </summary>
public static class ModelPathResolver
{
    private const int MaxParentWalkDepth = 8;

    /// <summary>
    /// Resolves the model path from explicit configuration, falling back to searching
    /// deployment and development locations for a <c>.gguf</c> file.
    /// </summary>
    /// <returns>The model file to load, or <c>null</c> when none exists.</returns>
    public static string? Resolve()
    {
        if (TryGetExplicitConfiguration(out var configured))
        {
            return ResolveExplicitPath(configured);
        }

        return ResolveBySearch();
    }

    private static bool TryGetExplicitConfiguration(out string configuredPath)
    {
        configuredPath = Environment.GetEnvironmentVariable("CODELENSAI_MODEL_PATH")?.Trim() ?? string.Empty;
        if (configuredPath.Length > 0)
        {
            return true;
        }

        try
        {
            var settingsPath = AppPaths.InAppData("settings.json");
            if (File.Exists(settingsPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath));
                if (doc.RootElement.TryGetProperty("modelPath", out var value) &&
                    value.ValueKind == JsonValueKind.String)
                {
                    configuredPath = value.GetString()?.Trim() ?? string.Empty;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A malformed or unreadable settings file falls back to the automatic search.
        }

        return configuredPath.Length > 0;
    }

    /// <summary>
    /// An explicitly configured path may point at a .gguf file or a directory containing one.
    /// </summary>
    private static string? ResolveExplicitPath(string configured)
    {
        if (File.Exists(configured))
        {
            return configured;
        }

        if (Directory.Exists(configured))
        {
            return TryFindGgufInDirectory(configured);
        }

        return null;
    }

    private static string? ResolveBySearch()
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

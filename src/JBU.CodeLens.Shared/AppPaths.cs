namespace JBU.CodeLens.Shared;

/// <summary>
/// Per-user filesystem locations under the roaming profile. Centralizes the application's
/// <c>%APPDATA%</c> folder name so the separate stores (model settings, AI result cache, custom
/// questions) resolve it identically and can never drift apart.
/// </summary>
public static class AppPaths
{
    /// <summary>The application's folder name under <c>%APPDATA%</c>.</summary>
    public const string AppDataFolderName = "JBU.CodeLens";

    /// <summary>
    /// The pre-rename folder name. Retained so an existing installation's settings and AI result
    /// cache carry over instead of silently resetting the user's theme, last project and every
    /// cached generation.
    /// </summary>
    private const string LegacyAppDataFolderName = "CodeLensAI";

    private static int _migrationAttempted;

    /// <summary>
    /// Builds the path to <paramref name="fileName"/> inside the application's <c>%APPDATA%</c>
    /// folder (for example <c>%APPDATA%\JBU.CodeLens\settings.json</c>), migrating the pre-rename
    /// folder across on first use.
    /// </summary>
    public static string InAppData(string fileName)
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            AppDataFolderName);

        MigrateLegacyFolderOnce(directory);
        return Path.Combine(directory, fileName);
    }

    /// <summary>
    /// Copies the pre-rename folder's contents across the first time a path is resolved in this
    /// process. Deliberately a copy rather than a move: an older build pointed at the legacy
    /// folder keeps working, so downgrading does not lose data. Runs at most once per process and
    /// only when the new folder does not yet exist, so it can never overwrite newer state.
    /// Best-effort throughout — a failed migration costs regenerated cache, never a broken launch.
    /// </summary>
    private static void MigrateLegacyFolderOnce(string currentDirectory)
    {
        if (Interlocked.Exchange(ref _migrationAttempted, 1) != 0)
        {
            return;
        }

        try
        {
            if (Directory.Exists(currentDirectory))
            {
                return;
            }

            var legacyDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                LegacyAppDataFolderName);

            if (!Directory.Exists(legacyDirectory))
            {
                return;
            }

            Directory.CreateDirectory(currentDirectory);
            foreach (var source in Directory.GetFiles(legacyDirectory))
            {
                File.Copy(source, Path.Combine(currentDirectory, Path.GetFileName(source)), overwrite: false);
            }
        }
        catch
        {
            // A failed migration must never block startup; the stores just begin empty.
        }
    }
}

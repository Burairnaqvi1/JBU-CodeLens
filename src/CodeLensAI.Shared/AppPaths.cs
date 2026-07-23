namespace CodeLensAI.Shared;

/// <summary>
/// Per-user filesystem locations under the roaming profile. Centralizes the application's
/// <c>%APPDATA%</c> folder name so the separate stores (model settings, AI result cache, custom
/// questions) resolve it identically and can never drift apart.
/// </summary>
public static class AppPaths
{
    /// <summary>The application's folder name under <c>%APPDATA%</c>.</summary>
    public const string AppDataFolderName = "CodeLensAI";

    /// <summary>
    /// Builds the path to <paramref name="fileName"/> inside the application's <c>%APPDATA%</c>
    /// folder (for example <c>%APPDATA%\CodeLensAI\settings.json</c>).
    /// </summary>
    public static string InAppData(string fileName) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        AppDataFolderName,
        fileName);
}

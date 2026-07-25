using System.IO;
using System.Text.Json;

namespace JBU.CodeLens.UI.Helpers;

/// <summary>
/// Small persisted UI preferences — the chosen theme and the last opened project — stored as
/// JSON in <c>%APPDATA%\JBU.CodeLens\ui-settings.json</c>. Best-effort: <see cref="Load"/> returns
/// defaults on any error and <see cref="Save"/> swallows failures, so a missing or corrupt file
/// never disrupts the app.
/// </summary>
public sealed class UiSettings
{
    /// <summary>"Dark" (default) or "Light".</summary>
    public string Theme { get; set; } = "Dark";

    /// <summary>Full path of the most recently scanned project folder, or null if none.</summary>
    public string? LastProjectPath { get; set; }

    private static string FilePath => AppPaths.InAppData("ui-settings.json");

    public static UiSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(FilePath));
                if (loaded is not null)
                {
                    return loaded;
                }
            }
        }
        catch
        {
            // Corrupt or unreadable settings must never block startup — fall back to defaults.
        }

        return new UiSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch
        {
            // Persistence is a convenience; losing it costs nothing functional.
        }
    }
}

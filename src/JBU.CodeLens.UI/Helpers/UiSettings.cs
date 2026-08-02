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

    /// <summary>
    /// Folder the last export was written to, so the save dialog opens where the user works
    /// rather than sending them back through the same folders on every export.
    /// </summary>
    public string? LastExportFolder { get; set; }

    /// <summary>
    /// Where the window was and how big it was when it last closed. Null until the first close.
    /// </summary>
    /// <remarks>
    /// Stored so the window opens where it was left rather than being recentred each launch,
    /// which undid any resizing the user had done. Kept as four nullable numbers rather than a
    /// rectangle so a settings file written by an older build still loads.
    /// </remarks>
    public double? WindowLeft { get; set; }

    public double? WindowTop { get; set; }

    public double? WindowWidth { get; set; }

    public double? WindowHeight { get; set; }

    /// <summary>True when the window was maximised at close.</summary>
    public bool WindowMaximized { get; set; }

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

            // Atomic: an interrupted write leaves the previous settings rather than a truncated
            // file, so a crash while saving cannot cost the user their theme and last project.
            AtomicFileWriter.Write(FilePath, temp => File.WriteAllText(temp, JsonSerializer.Serialize(this)));
        }
        catch
        {
            // Persistence is a convenience; losing it costs nothing functional.
        }
    }
}

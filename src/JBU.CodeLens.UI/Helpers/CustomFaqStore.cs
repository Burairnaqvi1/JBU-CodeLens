using System.IO;
using System.Text.Json;

namespace JBU.CodeLens.UI.Helpers;

/// <summary>
/// Persists user-added follow-up questions (shown alongside the built-in template questions in
/// the method detail panel) to a small JSON file under the user's AppData folder, so they survive
/// across app restarts. Global to the app, not per-method — kept simple since there's no clean
/// per-method identity to key on.
/// </summary>
public static class CustomFaqStore
{
    private static readonly string FilePath = AppPaths.InAppData("custom_questions.json");

    // In-memory cache so Load() — called on every method-detail render, on the UI thread —
    // normally costs a single timestamp stat instead of a read + JSON parse.
    private static List<string>? _cache;
    private static DateTime _cacheFileTimeUtc;

    public static List<string> Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                _cache = [];
                return _cache;
            }

            var fileTime = File.GetLastWriteTimeUtc(FilePath);
            if (_cache is not null && fileTime == _cacheFileTimeUtc)
            {
                return _cache;
            }

            var json = File.ReadAllText(FilePath);
            _cache = JsonSerializer.Deserialize<List<string>>(json) ?? [];
            _cacheFileTimeUtc = fileTime;
            return _cache;
        }
        catch
        {
            return _cache ?? [];
        }
    }

    /// <summary>Adds a question if non-empty and not already present. Returns the updated list.</summary>
    public static List<string> Add(string question)
    {
        var trimmed = question.Trim();
        var questions = Load();

        if (trimmed.Length == 0 || questions.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
        {
            return questions;
        }

        questions.Add(trimmed);
        Save(questions);
        return questions;
    }

    public static List<string> Remove(string question)
    {
        var questions = Load();
        questions.RemoveAll(q => string.Equals(q, question, StringComparison.OrdinalIgnoreCase));
        Save(questions);
        return questions;
    }

    private static void Save(List<string> questions)
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(FilePath, JsonSerializer.Serialize(questions));
            _cache = questions;
            _cacheFileTimeUtc = File.GetLastWriteTimeUtc(FilePath);
        }
        catch
        {
            // Best-effort persistence — a failed save shouldn't crash the UI.
        }
    }
}

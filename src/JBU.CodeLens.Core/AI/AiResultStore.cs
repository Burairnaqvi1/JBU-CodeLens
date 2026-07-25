using System.Text.Json;

namespace JBU.CodeLens.Core.AI;

/// <summary>
/// Persists the AI inference result cache across sessions as a JSON file in
/// <c>%APPDATA%\JBU.CodeLens</c>. Cache keys already include the source file's last-write time,
/// so a stale entry for an edited file is simply never hit again; pruning keeps the file from
/// growing without bound. The store is tied to one model: results from a different GGUF are
/// ignored on load, because a different model produces different text for the same key.
/// </summary>
public sealed class AiResultStore
{
    private const int SchemaVersion = 1;
    public const int MaxEntries = 4000;
    public const int TrimTarget = 3000;

    private readonly string _path;
    private readonly string _modelName;

    /// <summary>Creation timestamps of known entries, used to prune oldest-first on save.</summary>
    private readonly Dictionary<string, long> _createdTicks = new(StringComparer.Ordinal);

    private sealed class Entry
    {
        public string V { get; set; } = string.Empty;
        public long T { get; set; }
    }

    private sealed class FileShape
    {
        public int Version { get; set; }
        public string Model { get; set; } = string.Empty;
        public Dictionary<string, Entry> Entries { get; set; } = new(StringComparer.Ordinal);
    }

    public AiResultStore(string path, string modelName)
    {
        _path = path;
        _modelName = modelName;
    }

    /// <summary>The default store location: <c>%APPDATA%\JBU.CodeLens\ai-cache.json</c>.</summary>
    public static string DefaultPath => AppPaths.InAppData("ai-cache.json");

    /// <summary>
    /// Loads the persisted results. Returns an empty dictionary — never throws — when the file
    /// is missing, corrupt, from a different schema version, or from a different model.
    /// </summary>
    public IReadOnlyDictionary<string, string> Load()
    {
        var results = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(_path))
            {
                return results;
            }

            var shape = JsonSerializer.Deserialize<FileShape>(File.ReadAllText(_path));
            if (shape is null || shape.Version != SchemaVersion ||
                !string.Equals(shape.Model, _modelName, StringComparison.OrdinalIgnoreCase))
            {
                return results;
            }

            foreach (var (key, entry) in shape.Entries)
            {
                if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(entry.V))
                {
                    continue;
                }

                results[key] = entry.V;
                _createdTicks[key] = entry.T;
            }
        }
        catch
        {
            // A broken cache file must never break the app; inference just starts cold.
            results.Clear();
        }

        return results;
    }

    /// <summary>
    /// Saves the given results (typically the loaded entries plus this session's new ones),
    /// pruning oldest-first past <see cref="MaxEntries"/>. Best-effort: failures are swallowed —
    /// losing the cache costs regeneration time, nothing more. The write goes through a temp
    /// file + atomic move so a crash mid-save cannot corrupt the previous cache.
    /// </summary>
    public void Save(IEnumerable<KeyValuePair<string, string>> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        try
        {
            var now = DateTime.UtcNow.Ticks;
            var shape = new FileShape { Version = SchemaVersion, Model = _modelName };
            foreach (var (key, value) in results)
            {
                if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(value))
                {
                    continue;
                }

                shape.Entries[key] = new Entry
                {
                    V = value,
                    T = _createdTicks.TryGetValue(key, out var ticks) ? ticks : now,
                };
            }

            if (shape.Entries.Count > MaxEntries)
            {
                foreach (var key in shape.Entries
                             .OrderBy(pair => pair.Value.T)
                             .Take(shape.Entries.Count - TrimTarget)
                             .Select(pair => pair.Key)
                             .ToList())
                {
                    shape.Entries.Remove(key);
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            Export.AtomicFileWriter.Write(
                _path,
                tempPath => File.WriteAllText(tempPath, JsonSerializer.Serialize(shape)));
        }
        catch
        {
            // Best-effort persistence only.
        }
    }
}

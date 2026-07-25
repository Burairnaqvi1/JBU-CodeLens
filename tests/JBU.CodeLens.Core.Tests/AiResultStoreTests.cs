using JBU.CodeLens.Core.AI;

namespace JBU.CodeLens.Core.Tests;

/// <summary>
/// Tests for the persisted AI result cache. The store must round-trip results for the same
/// model, refuse results from a different model or a corrupt file, and prune oldest-first —
/// and never throw, because a broken cache file must not break the app.
/// </summary>
public class AiResultStoreTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("codelens-store-tests").FullName;

    private string StorePath => Path.Combine(_tempDir, "ai-cache.json");

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void SaveThenLoad_SameModel_RoundTrips()
    {
        var results = new Dictionary<string, string> { ["key1"] = "text one", ["key2"] = "text two" };
        new AiResultStore(StorePath, "model-a.gguf").Save(results);

        var loaded = new AiResultStore(StorePath, "model-a.gguf").Load();

        Assert.Equal(2, loaded.Count);
        Assert.Equal("text one", loaded["key1"]);
        Assert.Equal("text two", loaded["key2"]);
    }

    [Fact]
    public void Load_DifferentModel_ReturnsEmpty()
    {
        new AiResultStore(StorePath, "model-a.gguf").Save(
            new Dictionary<string, string> { ["key"] = "text" });

        Assert.Empty(new AiResultStore(StorePath, "model-b.gguf").Load());
    }

    [Fact]
    public void Load_MissingOrCorruptFile_ReturnsEmptyInsteadOfThrowing()
    {
        Assert.Empty(new AiResultStore(StorePath, "model-a.gguf").Load());

        File.WriteAllText(StorePath, "{ not valid json !!");
        Assert.Empty(new AiResultStore(StorePath, "model-a.gguf").Load());
    }

    [Fact]
    public void Save_OverCapacity_PrunesOldestFirst()
    {
        // First session writes MaxEntries entries; they all get the same (old) timestamp batch.
        var store = new AiResultStore(StorePath, "model-a.gguf");
        var old = new Dictionary<string, string>();
        for (var i = 0; i < AiResultStore.MaxEntries; i++)
        {
            old[$"old{i}"] = "x";
        }

        store.Save(old);

        // A later session adds fresh entries on top, pushing the total over the cap.
        var second = new AiResultStore(StorePath, "model-a.gguf");
        var merged = new Dictionary<string, string>(second.Load()) { ["fresh"] = "kept" };
        second.Save(merged);

        var final = new AiResultStore(StorePath, "model-a.gguf").Load();
        Assert.True(final.Count <= AiResultStore.TrimTarget);
        Assert.Equal("kept", final["fresh"]);
    }

    [Fact]
    public void Save_EmptyValues_AreSkipped()
    {
        new AiResultStore(StorePath, "model-a.gguf").Save(
            new Dictionary<string, string> { ["good"] = "text", ["empty"] = "" });

        var loaded = new AiResultStore(StorePath, "model-a.gguf").Load();
        Assert.Single(loaded);
        Assert.True(loaded.ContainsKey("good"));
    }
}

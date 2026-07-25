using JBU.CodeLens.Core.AI;

namespace JBU.CodeLens.Core.Tests;

public sealed class ModelPathResolverTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("codelens-model-tests").FullName;

    public ModelPathResolverTests()
    {
        Environment.SetEnvironmentVariable("JBU_CODELENS_MODEL_PATH", null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("JBU_CODELENS_MODEL_PATH", null);
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Resolve_EnvVarPointsAtFile_ReturnsThatFile()
    {
        var modelPath = Path.Combine(_tempDir, "configured.gguf");
        File.WriteAllText(modelPath, "stub");
        Environment.SetEnvironmentVariable("JBU_CODELENS_MODEL_PATH", modelPath);

        Assert.Equal(modelPath, ModelPathResolver.Resolve());
    }

    [Fact]
    public void Resolve_EnvVarPointsAtDirectory_ReturnsFirstGgufInside()
    {
        var modelPath = Path.Combine(_tempDir, "model.gguf");
        File.WriteAllText(modelPath, "stub");
        Environment.SetEnvironmentVariable("JBU_CODELENS_MODEL_PATH", _tempDir);

        Assert.Equal(modelPath, ModelPathResolver.Resolve());
    }

    [Fact]
    public void Resolve_EnvVarInvalid_FailsInsteadOfLoadingSomethingElse()
    {
        // The dev-tree fallback search would find the repo's real model; an explicit but
        // broken configuration must NOT silently fall back to it.
        Environment.SetEnvironmentVariable("JBU_CODELENS_MODEL_PATH", Path.Combine(_tempDir, "missing.gguf"));

        Assert.Null(ModelPathResolver.Resolve());
    }
}

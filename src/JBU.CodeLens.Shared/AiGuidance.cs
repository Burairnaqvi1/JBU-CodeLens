namespace JBU.CodeLens.Shared;

/// <summary>
/// User-facing AI guidance strings shared between Core (which detects the conditions) and the
/// UI (which displays them).
/// </summary>
public static class AiGuidance
{
    /// <summary>
    /// Shown when no GGUF model file could be located.
    /// </summary>
    public const string ModelNotFoundMessage =
        "Model not found. Place a .gguf model file in the models/ folder next to the application, " +
        "or set its location explicitly via the JBU_CODELENS_MODEL_PATH environment variable " +
        "or \"modelPath\" in %APPDATA%\\JBU.CodeLens\\settings.json.";
}

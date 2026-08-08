namespace JBU.CodeLens.Shared.Models;

/// <summary>
/// One of the AI-written sections of a method page.
/// </summary>
/// <remarks>
/// Each section is generated and cached separately, so the reader can ask for a fresh answer to
/// one of them without discarding the others. Regenerating the design requirements used to clear
/// every cached answer for the method, which threw away an explanation the reader was still
/// reading.
/// </remarks>
public enum AiSection
{
    /// <summary>The one-line description shown under the method name.</summary>
    Brief,

    /// <summary>Pre-conditions and post-conditions.</summary>
    PrePost,

    /// <summary>Design requirements.</summary>
    Design,

    /// <summary>Potential errors and exceptions.</summary>
    Errors,

    /// <summary>The multi-sentence explanation that seeds the Q&amp;A.</summary>
    Explanation,
}

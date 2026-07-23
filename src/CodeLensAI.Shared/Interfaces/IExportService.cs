using CodeLensAI.Shared.Structural;

namespace CodeLensAI.Shared.Interfaces;

/// <summary>
/// Documentation export in the supported formats. Implemented by Core's <c>ExportService</c>;
/// the UI consumes only this interface. All methods are synchronous and I/O-bound — callers
/// invoke them from a worker thread.
/// </summary>
public interface IExportService
{
    /// <summary>
    /// Exports full project documentation to a Word (.docx) file, optionally enriched with
    /// AI-generated sections (one merged model call per method).
    /// </summary>
    void ExportWord(
        string outputPath,
        string projectFolder,
        List<ParseResult> parseResults,
        IExplanationService? explanationService,
        bool includeAi,
        Action<string>? onProgress,
        ProjectMetricsSnapshot? metrics,
        CancellationToken cancellationToken = default);

    /// <summary>Exports the structural analysis (plus deterministic method analysis) to Markdown.</summary>
    void ExportMarkdown(ProjectIR ir, IReadOnlyList<ParseResult> parseResults, string outputPath);

    /// <summary>Exports the structural analysis (plus deterministic method analysis) to JSON.</summary>
    void ExportJson(ProjectIR ir, IReadOnlyList<ParseResult> parseResults, string outputPath);
}

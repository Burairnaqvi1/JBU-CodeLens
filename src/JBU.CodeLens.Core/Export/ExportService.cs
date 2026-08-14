using JBU.CodeLens.Shared.Structural;

namespace JBU.CodeLens.Core.Export;

/// <summary>
/// <see cref="IExportService"/> implementation that fronts the static exporters, giving the UI
/// a single interface for all documentation export formats.
/// </summary>
public sealed class ExportService : IExportService
{
    /// <inheritdoc />
    public void ExportWord(
        string outputPath,
        string projectFolder,
        List<ParseResult> parseResults,
        IExplanationService? explanationService,
        bool includeAi,
        Action<string>? onProgress,
        CancellationToken cancellationToken = default)
    {
        WordExporter.Export(outputPath, projectFolder, parseResults, explanationService, includeAi, onProgress, cancellationToken);
    }

    /// <inheritdoc />
    public void ExportMarkdown(ProjectIR ir, IReadOnlyList<ParseResult> parseResults, string outputPath) =>
        InferenceExportHelper.WriteMarkdownFile(ir, parseResults, outputPath);

    /// <inheritdoc />
    public void ExportJson(ProjectIR ir, IReadOnlyList<ParseResult> parseResults, string outputPath) =>
        InferenceExportHelper.WriteJsonFile(ir, parseResults, outputPath);
}

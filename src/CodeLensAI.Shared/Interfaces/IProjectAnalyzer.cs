using CodeLensAI.Shared.Structural;
using LensMethod = CodeLensAI.Shared.Models.MethodInfo;
using ScideMethod = CodeLensAI.Shared.Structural.MethodInfo;

namespace CodeLensAI.Shared.Interfaces;

/// <summary>
/// Project-wide structural analysis. Implemented by Core's <c>ScideEngine</c>; the UI consumes
/// only this interface.
/// </summary>
public interface IProjectAnalyzer
{
    /// <summary>
    /// Scans and analyzes every C#/C++ source file under <paramref name="path"/>. Parsing runs
    /// in parallel on worker threads; unchanged files are served from a cross-scan cache.
    /// Cancellation stops scheduling further file parses promptly and surfaces as a failed
    /// result (not an exception). Per-file progress is reported through
    /// <paramref name="progress"/> when supplied.
    /// </summary>
    Task<AnalysisResult> AnalyzeProjectAsync(
        string path,
        CancellationToken cancellationToken = default,
        IProgress<Models.ScanProgress>? progress = null);

    /// <summary>Metrics-based project summary used when the AI model is unavailable.</summary>
    string GetProjectSummaryFallback(ProjectIR ir);

    /// <summary>
    /// Assembles the unified detail-panel context for a method, including deterministic analysis
    /// (from the scan cache, or computed on demand) and preformatted display strings.
    /// </summary>
    MethodDetailContext BuildMethodDetailContext(
        LensMethod method,
        ProjectIR? ir,
        IReadOnlyDictionary<string, ScideMethod> methodIndex,
        IReadOnlyDictionary<string, TypeInfo> typeIndex);
}

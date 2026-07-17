using CodeLensAI.Shared.Models;

namespace CodeLensAI.Shared.Interfaces;

/// <summary>
/// AI documentation service backed by a local LLM. Implemented by Core's
/// <c>ExplanationService</c>; the UI consumes only this interface.
/// All generation methods are synchronous and potentially slow (seconds) — callers must invoke
/// them from a worker thread. Results for unchanged methods are served from a session cache.
/// </summary>
public interface IExplanationService : IDisposable
{
    /// <summary>True when the model loaded successfully and inference can be attempted.</summary>
    bool IsReady { get; }

    /// <summary>The resolved path to the GGUF model file.</summary>
    string ModelPath { get; }

    /// <summary>Why the model failed to load, or <c>null</c> when it loaded successfully.</summary>
    string? LoadError { get; }

    /// <summary>
    /// Number of actual model executions performed (cache hits excluded). Lets callers verify
    /// the inference cache: a fully cached pass leaves this flat.
    /// </summary>
    int InferenceCallCount { get; }

    /// <summary>2-3 sentence explanation of what the method does and returns.</summary>
    string ExplainMethod(MethodInfo methodInfo);

    /// <summary>One-sentence developer-style description.</summary>
    string GenerateBriefDescription(MethodInfo methodInfo);

    /// <summary>Short project overview from a pre-formatted context description.</summary>
    string GenerateProjectSummary(string projectContext);

    /// <summary>Pre/post-condition bullets.</summary>
    string GeneratePrePostConditions(MethodInfo methodInfo);

    /// <summary>Design-constraint bullets.</summary>
    string GenerateDesignConstraints(MethodInfo methodInfo);

    /// <summary>Potential-error bullets.</summary>
    string GenerateErrorAnalysis(MethodInfo methodInfo);

    /// <summary>
    /// All five documentation sections in a single model call. Honors
    /// <paramref name="cancellationToken"/> mid-inference (throws
    /// <see cref="OperationCanceledException"/>), since this is the bulk-export path where a
    /// cancel must take effect promptly rather than after the current method finishes.
    /// </summary>
    MethodAiDocumentation GenerateMethodDocumentation(MethodInfo methodInfo, CancellationToken cancellationToken = default);

    /// <summary>Starts a multi-turn Q&amp;A session about a method.</summary>
    IMethodConversationSession StartMethodConversation(MethodInfo methodInfo, string initialExplanation);
}

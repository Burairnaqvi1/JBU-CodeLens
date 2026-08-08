namespace JBU.CodeLens.Shared.Interfaces;

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

    /// <summary>
    /// 2-3 sentence explanation of what the method does and returns. When
    /// <paramref name="onPartial"/> is supplied it receives the accumulated raw text as the
    /// model streams tokens (from the inference thread — marshal to the UI thread before
    /// touching controls); the returned string is the final post-processed text and may differ
    /// slightly from the last partial.
    /// </summary>
    string ExplainMethod(MethodInfo methodInfo, Action<string>? onPartial = null, CancellationToken cancellationToken = default);

    /// <summary>One-sentence developer-style description. Streams like ExplainMethod.</summary>
    string GenerateBriefDescription(MethodInfo methodInfo, Action<string>? onPartial = null, CancellationToken cancellationToken = default);

    /// <summary>Short project overview from a pre-formatted context description.</summary>
    string GenerateProjectSummary(string projectContext);

    /// <summary>
    /// 2-3 sentence description of a class's responsibility, built from its verified members
    /// and relationships. Cached per class for the session, like the method operations.
    /// Streams like ExplainMethod.
    /// </summary>
    string GenerateClassSummary(ClassInfo classInfo, Action<string>? onPartial = null, CancellationToken cancellationToken = default);

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

    /// <summary>
    /// Discards every cached answer held for <paramref name="methodInfo"/>, so the next request
    /// for any of its sections reaches the model instead of the cache. Backs the "Regenerate"
    /// controls in the detail panel.
    /// </summary>
    void Forget(MethodInfo methodInfo);

    /// <summary>
    /// Discards one section's cached answer, leaving the method's other sections intact. Backs
    /// the per-section Regenerate controls.
    /// </summary>
    void Forget(MethodInfo methodInfo, AiSection section);
}

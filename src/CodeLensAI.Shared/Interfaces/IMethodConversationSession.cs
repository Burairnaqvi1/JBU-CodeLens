using CodeLensAI.Shared.Models;

namespace CodeLensAI.Shared.Interfaces;

/// <summary>
/// A multi-turn Q&amp;A conversation about a single method, seeded with an initial explanation.
/// </summary>
public interface IMethodConversationSession
{
    /// <summary>The question/answer exchanges so far.</summary>
    IReadOnlyList<ConversationTurn> History { get; }

    /// <summary>The explanation the session was seeded with.</summary>
    string InitialExplanation { get; }

    /// <summary>
    /// Asks a follow-up question. Synchronous and potentially slow — call from a worker thread.
    /// </summary>
    string Ask(string question);
}

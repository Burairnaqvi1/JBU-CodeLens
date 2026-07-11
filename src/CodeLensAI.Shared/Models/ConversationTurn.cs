namespace CodeLensAI.Shared.Models;

/// <summary>
/// A single question/answer exchange within a <see cref="MethodConversationSession"/>.
/// </summary>
public sealed class ConversationTurn
{
    /// <summary>The user's question.</summary>
    public string Question { get; }

    /// <summary>The model's answer.</summary>
    public string Answer { get; }

    /// <summary>Creates a turn from a question and its answer.</summary>
    public ConversationTurn(string question, string answer)
    {
        Question = question;
        Answer = answer;
    }
}

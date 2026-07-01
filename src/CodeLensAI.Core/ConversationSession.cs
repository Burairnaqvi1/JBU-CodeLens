using System.Text;

namespace CodeLensAI.Core;

/// <summary>
/// A single question/answer exchange within a <see cref="ConversationSession"/>.
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

/// <summary>
/// Holds the running context of a multi-turn conversation about a single class: the original class
/// description, the initial explanation, and every follow-up question/answer pair. Each new question
/// is answered using the full accumulated history so context builds up across turns.
/// </summary>
/// <remarks>
/// History is reconstructed explicitly on every <see cref="Ask"/> call (the whole transcript is fed
/// back into a fresh inference) rather than relying on hidden model/KV state. This keeps multi-turn
/// behavior deterministic and isolated from other conversations sharing the same model.
/// </remarks>
public sealed class ConversationSession
{
    private readonly ExplanationService _service;
    private readonly string _classContext;
    private readonly string _initialExplanation;
    private readonly List<ConversationTurn> _history = new();

    /// <summary>
    /// The accumulated question/answer turns, oldest first.
    /// </summary>
    public IReadOnlyList<ConversationTurn> History => _history;

    /// <summary>
    /// The explanation the conversation was seeded with.
    /// </summary>
    public string InitialExplanation => _initialExplanation;

    internal ConversationSession(ExplanationService service, ClassInfo classInfo, string initialExplanation)
    {
        _service = service;
        _classContext = ExplanationService.DescribeClass(classInfo);
        _initialExplanation = initialExplanation;
    }

    /// <summary>
    /// Sends the accumulated conversation history plus <paramref name="question"/> to the model and
    /// returns its answer, recording both in <see cref="History"/>.
    /// </summary>
    /// <param name="question">The follow-up question to ask.</param>
    /// <returns>The model's answer, or an error message string if inference fails. Never throws.</returns>
    public string Ask(string question)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            return "[Please enter a question.]";
        }

        var prompt = BuildConversationPrompt(question);
        var answer = _service.RunInstruction(prompt);
        _history.Add(new ConversationTurn(question, answer));
        return answer;
    }

    /// <summary>
    /// Builds the full instruction body: class context, the initial explanation, every prior
    /// question/answer pair, and finally the new question.
    /// </summary>
    private string BuildConversationPrompt(string question)
    {
        var builder = new StringBuilder();
        builder.AppendLine(
            "You are a senior software engineer answering questions about a specific C# class. Use " +
            "the class metadata and the conversation so far to answer the final question concisely " +
            "and accurately.");
        builder.AppendLine();
        builder.AppendLine("Class metadata:");
        builder.AppendLine(_classContext);
        builder.AppendLine($"Initial explanation: {_initialExplanation}");
        builder.AppendLine();

        if (_history.Count > 0)
        {
            builder.AppendLine("Conversation so far:");
            foreach (var turn in _history)
            {
                builder.AppendLine($"Q: {turn.Question}");
                builder.AppendLine($"A: {turn.Answer}");
            }

            builder.AppendLine();
        }

        builder.AppendLine($"New question: {question}");
        builder.Append("Answer:");
        return builder.ToString();
    }
}

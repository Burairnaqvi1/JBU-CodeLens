using System.Text;

namespace CodeLensAI.Core.AI;

/// <summary>
/// Multi-turn Q&amp;A about a single method, seeded with an initial explanation.
/// </summary>
public sealed class MethodConversationSession : IMethodConversationSession
{
  private readonly ExplanationService _service;
  private readonly string _methodContext;
  private readonly string _initialExplanation;
  private readonly string _languageSuffix;
  private readonly List<ConversationTurn> _history = new();

  public IReadOnlyList<ConversationTurn> History => _history;
  public string InitialExplanation => _initialExplanation;

  internal MethodConversationSession(ExplanationService service, MethodInfo methodInfo, string initialExplanation)
  {
    _service = service;
    _methodContext = ExplanationService.DescribeMethod(methodInfo);
    _initialExplanation = initialExplanation;
    _languageSuffix = ExplanationService.GetLanguageContext(methodInfo);
  }

  private static readonly string[] DetailRequestKeywords =
  [
      "detail", "detailed", "elaborate", "in depth", "in-depth", "thorough",
      "thoroughly", "comprehensive", "explain fully", "fully explain",
      "step by step", "step-by-step", "at length", "more detail",
  ];

  public string Ask(string question, Action<string>? onPartial = null)
  {
    if (string.IsNullOrWhiteSpace(question))
    {
      return "[Please enter a question.]";
    }

    // Most follow-up questions are short factual asks and stay within the default token cap.
    // Only pay the extra generation-time cost when the question itself signals it wants a
    // longer, more thorough answer (e.g. "explain in detail", "step by step").
    var wantsDetail = WantsDetailedAnswer(question);
    var maxTokens = wantsDetail
      ? ExplanationService.MaxTokensFollowUpAnswerDetailed
      : ExplanationService.MaxTokensFollowUpAnswer;

    var prompt = BuildConversationPrompt(question, wantsDetail);
    var answer = _service.RunInstruction(prompt, maxTokens, onPartial);
    _history.Add(new ConversationTurn(question, answer));
    return answer;
  }

  private static bool WantsDetailedAnswer(string question) =>
    DetailRequestKeywords.Any(keyword => question.Contains(keyword, StringComparison.OrdinalIgnoreCase));

  private string BuildConversationPrompt(string question, bool wantsDetail)
  {
    var builder = new StringBuilder();
    builder.AppendLine(wantsDetail
      ? "Answer the question about this method using the metadata below. Give a detailed, thorough answer that covers relevant edge cases."
      : "Answer the question about this method using the metadata below. Be concise.");
    if (!string.IsNullOrEmpty(_languageSuffix))
    {
      builder.AppendLine(_languageSuffix);
    }

    builder.AppendLine();
    builder.AppendLine(_methodContext);
    builder.AppendLine($"Initial explanation: {_initialExplanation}");
    builder.AppendLine();

    if (_history.Count > 0)
    {
      // Only the most recent turns go into the prompt. The full history is unbounded, and the
      // input-budget overflow handling trims from the END of the instruction — where the new
      // question sits — so an oversized history would silently destroy the question itself.
      const int maxHistoryTurns = 4;
      builder.AppendLine("Conversation so far:");
      foreach (var turn in _history.Skip(Math.Max(0, _history.Count - maxHistoryTurns)))
      {
        builder.AppendLine($"Q: {turn.Question}");
        builder.AppendLine($"A: {turn.Answer}");
      }

      builder.AppendLine();
    }

    builder.AppendLine($"Question: {question}");
    builder.Append("Answer:");
    return builder.ToString();
  }
}

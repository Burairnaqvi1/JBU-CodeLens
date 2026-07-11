using System.Text;
using LLama;
using LLama.Common;
using LLama.Sampling;

namespace CodeLensAI.Core;

/// <summary>
/// Runs local, on-device inference against a GGUF model (for example CodeLlama-Instruct) using
/// LLamaSharp, to generate plain-English explanations of parsed classes.
/// </summary>
/// <remarks>
/// The model <em>weights</em> are loaded once in the constructor (this is the expensive step and
/// holds significant native memory). Each individual inference then spins up a fresh
/// <see cref="LLamaContext"/> and <see cref="InstructExecutor"/> and disposes them afterwards.
/// Creating a context per call is deliberately chosen over sharing one long-lived context: a
/// shared context accumulates KV-cache state between unrelated prompts, which both pollutes later
/// outputs and eventually overflows the context window. Per-call contexts keep each explanation
/// independent and predictable, while still paying the heavy model-load cost only once.
/// </remarks>
public sealed class ExplanationService : IDisposable
{
    private const int ContextSize = 2048;

    private const int MaxTokensBrief = 50;
    private const int MaxTokensBullets = 250;
    private const int MaxTokensErrors = 150;
    private const int MaxTokensExplanation = 400;
    private const int MaxTokensFollowUp = 200;
    private const int MaxTokensFollowUpDetailed = 600;
    private const int MaxSourceSnippetForAi = 400;
    private const int MaxSourceSnippetBrief = 300;

    internal const int MaxTokensFollowUpAnswer = MaxTokensFollowUp;
    internal const int MaxTokensFollowUpAnswerDetailed = MaxTokensFollowUpDetailed;

    private const string DefaultSystemPrompt =
        "You are a concise technical writer. Answer briefly and stay factual.";

    private readonly LLamaWeights? _weights;
    private readonly ModelParams? _modelParams;
    private readonly bool _usesPhiTemplate;
    private readonly bool _usesChatMlTemplate;

    // Serializes all inference. Each call creates its own LLamaContext (a fresh KV-cache
    // allocation) and uses ProcessorCount-1 threads; letting two calls overlap oversubscribes
    // the CPU and spikes native memory, making both calls slower than running them in sequence.
    private readonly SemaphoreSlim _inferenceLock = new(1, 1);
    private bool _disposed;

    /// <summary>
    /// True when the model loaded successfully and inference can be attempted.
    /// </summary>
    public bool IsReady { get; }

    /// <summary>
    /// The resolved path to the GGUF model file.
    /// </summary>
    public string ModelPath { get; }

    /// <summary>
    /// A human-readable description of why the model failed to load, or <c>null</c> when it loaded
    /// successfully.
    /// </summary>
    public string? LoadError { get; }

    /// <summary>
    /// Loads the GGUF model at <paramref name="modelPath"/>. If the file is missing or invalid the
    /// service is left in a not-ready state with <see cref="LoadError"/> populated, rather than
    /// throwing, so the host application can surface the problem gracefully.
    /// </summary>
    /// <param name="modelPath">Absolute path to a <c>.gguf</c> model file.</param>
    public ExplanationService(string modelPath)
    {
        ModelPath = modelPath;
        var fileName = Path.GetFileName(modelPath);
        _usesPhiTemplate = fileName.Contains("phi", StringComparison.OrdinalIgnoreCase);
        _usesChatMlTemplate = fileName.Contains("qwen", StringComparison.OrdinalIgnoreCase);

        try
        {
            if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
            {
                LoadError = $"Model file not found at '{modelPath}'.";
                return;
            }

            // ContextSize kept conservative (2048) and GpuLayerCount = 0 for CPU-only inference.
            // Threads are pinned to the available CPU cores; leaving this unset makes LLamaSharp
            // fall back to a conservative default that badly under-utilizes the CPU and is the
            // main reason inference felt slow.
            var threadCount = Math.Max(1, Environment.ProcessorCount - 1);
            _modelParams = new ModelParams(modelPath)
            {
                ContextSize = ContextSize,
                GpuLayerCount = 0,
                Threads = threadCount,
                BatchThreads = threadCount,
            };

            _weights = LLamaWeights.LoadFromFile(_modelParams);
            IsReady = true;
        }
        catch (Exception ex)
        {
            LoadError = $"Failed to load model: {ex.Message}";
            IsReady = false;
        }
    }

    /// <summary>
    /// Generates a plain-English explanation of what <paramref name="methodInfo"/> is responsible for.
    /// </summary>
    public string ExplainMethod(MethodInfo methodInfo)
    {
        if (!IsReady)
        {
            return LoadError ?? "The explanation model is not available.";
        }

        var prompt = BuildExplainMethodPrompt(methodInfo);
        return TruncateProse(RunInstruction(prompt, MaxTokensExplanation), maxSentences: 3, maxWords: 80);
    }

    /// <summary>
    /// Generates a brief developer-style description when no XML summary exists.
    /// </summary>
    public string GenerateBriefDescription(MethodInfo methodInfo)
    {
        if (!IsReady)
        {
            return LoadError ?? "The explanation model is not available.";
        }

        var source = GetMethodSourceSnippet(methodInfo, MaxSourceSnippetBrief);
        var userPrompt =
            $"Method: {methodInfo.Name}, Returns: {methodInfo.ReturnType}, " +
            $"Params: {string.Join(", ", methodInfo.Parameters)}, Source: {source}";

        var systemPrompt = "Write one sentence describing what this C++ or C# method does.";
        var languageSuffix = GetLanguageSystemSuffix(methodInfo);
        if (!string.IsNullOrEmpty(languageSuffix))
        {
            systemPrompt += " " + languageSuffix;
        }

        return TruncateProse(RunInstruction(userPrompt, MaxTokensBrief, systemPrompt), maxSentences: 1, maxWords: 35);
    }

    /// <summary>
    /// Generates a short, plain-English project overview from a caller-supplied, pre-formatted
    /// description (for example project name, metrics, and key namespaces). Runs on the single
    /// already-loaded model so no second engine has to be spun up.
    /// </summary>
    public string GenerateProjectSummary(string projectContext)
    {
        if (!IsReady)
        {
            return LoadError ?? "The explanation model is not available.";
        }

        var systemPrompt = "You are a concise technical writer. Summarize the software project " +
                           "in 2-4 sentences: its purpose, structure, and notable characteristics. Stay factual.";
        return TruncateProse(
            RunInstruction(projectContext, MaxTokensExplanation, systemPrompt),
            maxSentences: 4,
            maxWords: 110);
    }

    /// <summary>
    /// Generates pre-condition and post-condition bullet points for a method.
    /// </summary>
    public string GeneratePrePostConditions(MethodInfo methodInfo)
    {
        if (!IsReady)
        {
            return LoadError ?? "The explanation model is not available.";
        }

        var prompt = BuildBulletPrompt(
            "List pre-conditions then post-conditions. Max 3 bullets per section. Format each line as '- text'.",
            DescribeMethodCompact(methodInfo),
            methodInfo);
        return TruncateBullets(RunInstruction(prompt, MaxTokensBullets), maxBullets: 6, maxWordsPerBullet: 14);
    }

    /// <summary>
    /// Generates design-constraint bullet points for a method.
    /// </summary>
    public string GenerateDesignConstraints(MethodInfo methodInfo)
    {
        if (!IsReady)
        {
            return LoadError ?? "The explanation model is not available.";
        }

        var prompt = BuildBulletPrompt(
            "List up to 4 design constraints as '- ' bullets. Cover state, dependencies, and side effects only when relevant.",
            DescribeMethodCompact(methodInfo),
            methodInfo);
        return TruncateBullets(RunInstruction(prompt, MaxTokensBullets), maxBullets: 4, maxWordsPerBullet: 14);
    }

    /// <summary>
    /// Generates potential errors and exceptions as bullet points.
    /// </summary>
    public string GenerateErrorAnalysis(MethodInfo methodInfo)
    {
        if (!IsReady)
        {
            return LoadError ?? "The explanation model is not available.";
        }

        var prompt = BuildBulletPrompt(
            "Only identify errors actually possible given the source code. " +
            "Do not list generic errors unrelated to this method. " +
            "If the method is simple with no error conditions, say so. Max 4 '- ' bullets.",
            DescribeMethodCompact(methodInfo),
            methodInfo);
        return TruncateBullets(RunInstruction(prompt, MaxTokensErrors), maxBullets: 4, maxWordsPerBullet: 16);
    }

    /// <summary>
    /// Starts a multi-turn conversation about a method.
    /// </summary>
    public MethodConversationSession StartMethodConversation(MethodInfo methodInfo, string initialExplanation)
    {
        return new MethodConversationSession(this, methodInfo, initialExplanation);
    }

    /// <summary>
    /// Runs a single instruction through a fresh context/executor and returns the full response
    /// text. Catches all failures and returns them as a message string so callers never crash.
    /// </summary>
    internal string RunInstruction(string instruction) =>
        RunInstruction(instruction, MaxTokensExplanation);

    internal string RunInstruction(string instruction, int maxTokens) =>
        RunInstruction(instruction, maxTokens, DefaultSystemPrompt);

    internal string RunInstruction(string instruction, int maxTokens, string systemPrompt)
    {
        if (!IsReady || _weights is null || _modelParams is null)
        {
            return LoadError ?? "The explanation model is not available.";
        }

        try
        {
            return Task.Run(() => RunInstructionAsync(instruction, maxTokens, systemPrompt)).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            return $"[Inference failed: {ex.Message}]";
        }
    }

    private async Task<string> RunInstructionAsync(string instruction, int maxTokens, string systemPrompt)
    {
        await _inferenceLock.WaitAsync().ConfigureAwait(false);
        try
        {
            return await RunInstructionLockedAsync(instruction, maxTokens, systemPrompt).ConfigureAwait(false);
        }
        finally
        {
            _inferenceLock.Release();
        }
    }

    private async Task<string> RunInstructionLockedAsync(string instruction, int maxTokens, string systemPrompt)
    {
        // Fresh context + executor per call; disposed promptly to release native memory.
        using var context = _weights!.CreateContext(_modelParams!);

        var inferenceParams = new InferenceParams
        {
            MaxTokens = maxTokens,
            SamplingPipeline = new DefaultSamplingPipeline
            {
                Temperature = 0.1f,
            },
        };

        var builder = new StringBuilder();

        if (_usesPhiTemplate)
        {
            inferenceParams.AntiPrompts = new[] { "<|end|>", "<|user|>", "<|system|>" };
            var prompt = FormatPhiPrompt(systemPrompt, instruction);
            var executor = new InstructExecutor(context, string.Empty, string.Empty);
            await foreach (var token in executor.InferAsync(prompt, inferenceParams))
            {
                builder.Append(token);
            }
        }
        else if (_usesChatMlTemplate)
        {
            inferenceParams.AntiPrompts = new[] { "<|im_end|>", "<|im_start|>" };
            var prompt = FormatChatMlPrompt(systemPrompt, instruction);
            var executor = new InstructExecutor(context, string.Empty, string.Empty);
            await foreach (var token in executor.InferAsync(prompt, inferenceParams))
            {
                builder.Append(token);
            }
        }
        else
        {
            // CodeLlama-Instruct expects the "[INST] ... [/INST]" wrapper.
            inferenceParams.AntiPrompts = new[] { "[INST]", "</s>" };
            var executor = new InstructExecutor(context, "[INST] ", " [/INST]");
            await foreach (var token in executor.InferAsync(instruction, inferenceParams))
            {
                builder.Append(token);
            }
        }

        return CleanResponse(builder.ToString(), _usesPhiTemplate, _usesChatMlTemplate);
    }

    private static string FormatPhiPrompt(string systemMessage, string userMessage) =>
        $"<|system|>{systemMessage}<|end|><|user|>{userMessage}<|end|><|assistant|>";

    private static string FormatChatMlPrompt(string systemMessage, string userMessage) =>
        $"<|im_start|>system\n{systemMessage}<|im_end|>\n<|im_start|>user\n{userMessage}<|im_end|>\n<|im_start|>assistant\n";

    /// <summary>
    /// Builds the instruction body describing the method and asking for a short explanation.
    /// </summary>
    private static string BuildExplainMethodPrompt(MethodInfo methodInfo)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Write 2-3 sentences summarizing what this method does and what it returns.");
        var languageSuffix = GetLanguageSystemSuffix(methodInfo);
        if (!string.IsNullOrEmpty(languageSuffix))
        {
            builder.AppendLine(languageSuffix);
        }

        builder.AppendLine();
        builder.Append(DescribeMethodCompact(methodInfo));
        return builder.ToString();
    }

    private static string BuildBulletPrompt(string instruction, string metadata, MethodInfo methodInfo)
    {
        var builder = new StringBuilder();
        builder.AppendLine(instruction);
        var languageSuffix = GetLanguageSystemSuffix(methodInfo);
        if (!string.IsNullOrEmpty(languageSuffix))
        {
            builder.AppendLine(languageSuffix);
        }

        builder.AppendLine();
        builder.Append(metadata);
        return builder.ToString();
    }

    internal static string GetLanguageContext(MethodInfo methodInfo) =>
        GetLanguageSystemSuffix(methodInfo);

    private static string GetLanguageSystemSuffix(MethodInfo methodInfo)
    {
        var sourcePath = methodInfo.ParentClass?.SourceFilePath;
        if (string.IsNullOrEmpty(sourcePath))
        {
            return string.Empty;
        }

        if (LanguageFileExtensions.IsCppFile(sourcePath))
        {
            return "This is a C++ method. Never mention null checks for primitive types " +
                   "(int, double, float, bool, char). Focus on value ranges, mathematical " +
                   "constraints, and state changes.";
        }

        if (LanguageFileExtensions.IsCSharpFile(sourcePath))
        {
            return "This is a C# method.";
        }

        return string.Empty;
    }

    internal static string GetMethodSourceSnippet(MethodInfo methodInfo, int maxLength = MaxSourceSnippetForAi)
    {
        if (!methodInfo.XmlDocTags.TryGetValue("sourceCode", out var source) || string.IsNullOrWhiteSpace(source))
        {
            return string.Empty;
        }

        source = source.Trim();
        return source.Length <= maxLength ? source : source[..maxLength];
    }

    /// <summary>
    /// Compact method metadata for prompts — keeps token count low for faster inference.
    /// </summary>
    internal static string DescribeMethodCompact(MethodInfo methodInfo)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Method: {methodInfo.ReturnType} {methodInfo.Name}({string.Join(", ", methodInfo.Parameters)})");
        builder.AppendLine($"Class: {methodInfo.ParentClass?.Name ?? "unknown"}");

        if (!string.IsNullOrEmpty(methodInfo.XmlSummary))
            builder.AppendLine($"Summary: {methodInfo.XmlSummary}");

        if (methodInfo.ThrownExceptions.Count > 0)
            builder.AppendLine($"Throws: {string.Join(", ", methodInfo.ThrownExceptions)}");

        if (methodInfo.OperationalLimits.Count > 0)
            builder.AppendLine($"Guards: {string.Join("; ", methodInfo.OperationalLimits.Take(3))}");

        var source = GetMethodSourceSnippet(methodInfo);
        if (!string.IsNullOrEmpty(source))
            builder.AppendLine($"Source: {source}");

        return builder.ToString();
    }

    /// <summary>
    /// Produces a compact, structured textual description of a method for use inside prompts.
    /// </summary>
    internal static string DescribeMethod(MethodInfo methodInfo)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Method name: {methodInfo.Name}");
        builder.AppendLine($"Return type: {methodInfo.ReturnType}");
        builder.AppendLine($"Access: {methodInfo.AccessModifier}");
        builder.AppendLine($"Parent class: {methodInfo.ParentClass?.Name ?? "unknown"}");
        builder.AppendLine(
            $"Parameters: {(methodInfo.Parameters.Count > 0 ? string.Join(", ", methodInfo.Parameters) : "none")}");

        if (!string.IsNullOrEmpty(methodInfo.XmlSummary))
        {
            builder.AppendLine($"Existing doc summary: {methodInfo.XmlSummary}");
        }

        if (methodInfo.ThrownExceptions.Count > 0)
        {
            builder.AppendLine($"Thrown exceptions: {string.Join(", ", methodInfo.ThrownExceptions)}");
        }

        if (methodInfo.OperationalLimits.Count > 0)
        {
            builder.AppendLine("Operational limits from code:");
            foreach (var limit in methodInfo.OperationalLimits)
            {
                builder.AppendLine($"  - {limit}");
            }
        }

        var globals = methodInfo.ParentClass?.Fields ?? [];
        if (globals.Count > 0)
        {
            builder.AppendLine("Class fields:");
            foreach (var field in globals)
            {
                builder.AppendLine($"  {field.AccessModifier} {field.Type} {field.Name}");
            }
        }

        if (methodInfo.LocalVariables.Count > 0)
        {
            builder.AppendLine("Local variables:");
            foreach (var local in methodInfo.LocalVariables)
            {
                builder.AppendLine($"  {local.Type} {local.Name}");
            }
        }

        var source = GetMethodSourceSnippet(methodInfo);
        if (!string.IsNullOrEmpty(source))
        {
            builder.AppendLine($"Source: {source}");
        }

        return builder.ToString();
    }

    /// <summary>
    /// Trims whitespace and strips any trailing instruction/end markers that leak into the output.
    /// </summary>
    private static string CleanResponse(string text, bool usesPhiTemplate, bool usesChatMlTemplate)
    {
        var cleaned = text.Trim();

        var markers = usesPhiTemplate
            ? new[] { "<|end|>", "<|user|>", "<|system|>", "<|assistant|>" }
            : usesChatMlTemplate
                ? new[] { "<|im_end|>", "<|im_start|>" }
                : new[] { "[INST]", "</s>" };

        foreach (var marker in markers)
        {
            var index = cleaned.IndexOf(marker, StringComparison.Ordinal);
            if (index >= 0)
            {
                cleaned = cleaned[..index].Trim();
            }
        }

        return cleaned.Length == 0 ? "[The model returned an empty response.]" : cleaned;
    }

  private static string TruncateProse(string text, int maxSentences, int maxWords)
  {
    if (text.StartsWith('[')) return text;

    var sentences = new List<string>();
    var current = new StringBuilder();
    foreach (var ch in text)
    {
      current.Append(ch);
      if (ch is '.' or '!' or '?')
      {
        var s = current.ToString().Trim();
        if (s.Length > 0) sentences.Add(s);
        current.Clear();
        if (sentences.Count >= maxSentences) break;
      }
    }

    var remainder = current.ToString().Trim();
    if (sentences.Count < maxSentences && remainder.Length > 0)
      sentences.Add(remainder);

    var result = string.Join(' ', sentences);
    var words = result.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    if (words.Length > maxWords)
      result = string.Join(' ', words.Take(maxWords)) + "…";

    return result;
  }

  private static string TruncateBullets(string text, int maxBullets, int maxWordsPerBullet)
  {
    if (text.StartsWith('[')) return text;

    var lines = new List<string>();
    foreach (var rawLine in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
      var line = rawLine.TrimStart('-', '•', '*', ' ');
      if (string.IsNullOrWhiteSpace(line)) continue;

      var words = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
      if (words.Length > maxWordsPerBullet)
        line = string.Join(' ', words.Take(maxWordsPerBullet)) + "…";

      lines.Add($"- {line}");
      if (lines.Count >= maxBullets) break;
    }

    return lines.Count > 0 ? string.Join(Environment.NewLine, lines) : text;
  }

    /// <summary>
    /// Releases the native model weights. Safe to call multiple times.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _weights?.Dispose();
        _inferenceLock.Dispose();
        _disposed = true;
    }
}

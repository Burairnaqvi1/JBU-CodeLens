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
    private const int MaxResponseTokens = 400;

    private readonly LLamaWeights? _weights;
    private readonly ModelParams? _modelParams;
    private bool _disposed;

    /// <summary>
    /// True when the model loaded successfully and inference can be attempted.
    /// </summary>
    public bool IsReady { get; }

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
        try
        {
            if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
            {
                LoadError = $"Model file not found at '{modelPath}'.";
                return;
            }

            // ContextSize kept conservative (2048) and GpuLayerCount = 0 for CPU-only inference.
            _modelParams = new ModelParams(modelPath)
            {
                ContextSize = ContextSize,
                GpuLayerCount = 0,
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
    /// Generates a 2-3 sentence, plain-English explanation of what <paramref name="classInfo"/> is
    /// responsible for, suitable for generated documentation.
    /// </summary>
    /// <param name="classInfo">The parsed class to explain.</param>
    /// <returns>
    /// The model's explanation, or a clear error message string if the model is unavailable or
    /// inference fails. This method never throws.
    /// </returns>
    public string ExplainClass(ClassInfo classInfo)
    {
        if (!IsReady)
        {
            return LoadError ?? "The explanation model is not available.";
        }

        var prompt = BuildExplainPrompt(classInfo);
        return RunInstruction(prompt);
    }

    /// <summary>
    /// Starts a multi-turn conversation seeded with the class's context and an initial explanation,
    /// allowing the user to ask follow-up questions about the class.
    /// </summary>
    /// <param name="classInfo">The class the conversation is about.</param>
    /// <param name="initialExplanation">The explanation already produced for the class.</param>
    /// <returns>A <see cref="ConversationSession"/> that accumulates history across questions.</returns>
    public ConversationSession StartConversation(ClassInfo classInfo, string initialExplanation)
    {
        return new ConversationSession(this, classInfo, initialExplanation);
    }

    /// <summary>
    /// Runs a single instruction through a fresh context/executor and returns the full response
    /// text. Catches all failures and returns them as a message string so callers never crash.
    /// </summary>
    internal string RunInstruction(string instruction)
    {
        if (!IsReady || _weights is null || _modelParams is null)
        {
            return LoadError ?? "The explanation model is not available.";
        }

        try
        {
            // Bridge the async streaming API to a synchronous result. Running on the thread pool
            // (rather than blocking an arbitrary captured context) avoids UI-thread deadlocks when
            // callers invoke this synchronously.
            return Task.Run(() => RunInstructionAsync(instruction)).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            return $"[Inference failed: {ex.Message}]";
        }
    }

    private async Task<string> RunInstructionAsync(string instruction)
    {
        // Fresh context + executor per call; disposed promptly to release native memory.
        using var context = _weights!.CreateContext(_modelParams!);

        // CodeLlama-Instruct expects the "[INST] ... [/INST]" wrapper; configure the InstructExecutor
        // to emit exactly that around each instruction.
        var executor = new InstructExecutor(context, "[INST] ", " [/INST]");

        var inferenceParams = new InferenceParams
        {
            MaxTokens = MaxResponseTokens,
            AntiPrompts = new[] { "[INST]", "</s>" },
            SamplingPipeline = new DefaultSamplingPipeline
            {
                Temperature = 0.2f,
            },
        };

        var builder = new StringBuilder();
        await foreach (var token in executor.InferAsync(instruction, inferenceParams))
        {
            builder.Append(token);
        }

        return CleanResponse(builder.ToString());
    }

    /// <summary>
    /// Builds the instruction body (without the [INST] wrapper, which the executor adds) describing
    /// the class and asking for a short explanation.
    /// </summary>
    private static string BuildExplainPrompt(ClassInfo classInfo)
    {
        var builder = new StringBuilder();
        builder.AppendLine(
            "You are a senior software engineer writing documentation. Based on the following C# " +
            "class metadata, explain in 2-3 plain English sentences what this class is responsible " +
            "for. Write for a developer reading generated documentation. Do not list the metadata " +
            "back; summarize the class's purpose.");
        builder.AppendLine();
        builder.Append(DescribeClass(classInfo));
        return builder.ToString();
    }

    /// <summary>
    /// Produces a compact, structured textual description of a class for use inside prompts.
    /// </summary>
    internal static string DescribeClass(ClassInfo classInfo)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Class name: {classInfo.Name}");
        builder.AppendLine($"Category: {DescribeCategory(classInfo.Category)}");
        builder.AppendLine($"Base class: {(string.IsNullOrEmpty(classInfo.BaseClassName) ? "none" : classInfo.BaseClassName)}");
        builder.AppendLine(
            $"Implements: {(classInfo.ImplementedInterfaces.Count > 0 ? string.Join(", ", classInfo.ImplementedInterfaces) : "none")}");
        builder.AppendLine(
            $"Dependencies: {(classInfo.Dependencies.Count > 0 ? string.Join(", ", classInfo.Dependencies) : "none")}");

        if (!string.IsNullOrEmpty(classInfo.XmlSummary))
        {
            builder.AppendLine($"Existing doc summary: {classInfo.XmlSummary}");
        }

        if (classInfo.Methods.Count > 0)
        {
            builder.AppendLine("Methods:");
            foreach (var method in classInfo.Methods)
            {
                var parameters = string.Join(", ", method.Parameters);
                builder.AppendLine($"  {method.AccessModifier} {method.ReturnType} {method.Name}({parameters})");
            }
        }

        if (classInfo.Properties.Count > 0)
        {
            builder.AppendLine("Properties:");
            foreach (var property in classInfo.Properties)
            {
                builder.AppendLine($"  {property.AccessModifier} {property.Type} {property.Name}");
            }
        }

        return builder.ToString();
    }

    private static string DescribeCategory(CodeCategory category) => category switch
    {
        CodeCategory.GuiLogic => "GUI logic",
        CodeCategory.Utility => "Utility",
        _ => "Business logic",
    };

    /// <summary>
    /// Trims whitespace and strips any trailing instruction/end markers that leak into the output.
    /// </summary>
    private static string CleanResponse(string text)
    {
        var cleaned = text.Trim();

        foreach (var marker in new[] { "[INST]", "</s>" })
        {
            var index = cleaned.IndexOf(marker, StringComparison.Ordinal);
            if (index >= 0)
            {
                cleaned = cleaned[..index].Trim();
            }
        }

        return cleaned.Length == 0 ? "[The model returned an empty response.]" : cleaned;
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
        _disposed = true;
    }
}

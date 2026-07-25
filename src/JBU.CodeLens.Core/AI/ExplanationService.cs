using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

using LLama;
using LLama.Common;
using LLama.Sampling;

namespace JBU.CodeLens.Core.AI;

/// <summary>
/// Runs local, on-device inference against a GGUF model (for example CodeLlama-Instruct) using
/// LLamaSharp, to generate plain-English explanations of parsed classes.
/// </summary>
/// <remarks>
/// The model <em>weights</em> are loaded once in the constructor (this is the expensive step and
/// holds significant native memory). A single <see cref="LLamaContext"/> is then kept for the
/// service's lifetime and reused for every inference: its KV cache is cleared
/// (<c>MemoryClear</c>) and a fresh executor is created before each call, so prompts stay fully
/// isolated from each other without paying the context's KV/compute-buffer allocation
/// (~360 MB native churn) on every call. All inference is serialized by a semaphore, which also
/// guards the shared context. Successful results are cached per session keyed on
/// (file path, method signature, file last-write time), so repeated requests for an unchanged
/// method skip the model entirely.
/// </remarks>
public sealed class ExplanationService : IExplanationService
{
    private const int ContextSize = 2048;

    private const int MaxTokensBrief = 50;
    private const int MaxTokensBullets = 300;
    private const int MaxTokensErrors = 180;
    private const int MaxTokensExplanation = 400;
    private const int MaxTokensFollowUp = 200;
    private const int MaxTokensFollowUpDetailed = 600;
    private const int MaxSourceSnippetForAi = 800;
    private const int MaxSourceSnippetBrief = 500;

    internal const int MaxTokensFollowUpAnswer = MaxTokensFollowUp;
    internal const int MaxTokensFollowUpAnswerDetailed = MaxTokensFollowUpDetailed;

    private const string DefaultSystemPrompt =
        "You are a concise technical writer. Answer briefly and stay factual.";

    // Input budget sized so the enlarged source snippet plus the verified-facts block always
    // fit: 1024 input + 800 merged output stays under the 2048-token context.
    private const int MaxInputTokens = 1024;
    private const int MaxTokensMergedDocumentation = 800;

    private readonly LLamaWeights? _weights;
    private readonly ModelParams? _modelParams;
    private readonly bool _usesPhiTemplate;
    private readonly bool _usesChatMlTemplate;

    // Serializes all inference and guards the shared context below. Inference uses
    // ProcessorCount-1 threads; letting two calls overlap oversubscribes the CPU and spikes
    // native memory, making both calls slower than running them in sequence.
    private readonly SemaphoreSlim _inferenceLock = new(1, 1);

    // Created lazily on first inference (inside the lock) and reused for every call with its
    // KV cache cleared between calls. Disposed with the service.
    private LLamaContext? _context;

    // Session-scoped inference results keyed on a stable hash of
    // (operation, file path, file last-write time, method signature). Only clean model output
    // is stored — bracketed error strings are never cached.
    private readonly ConcurrentDictionary<string, string> _resultCache = new();
    private readonly AiResultStore? _persistentStore;

    private int _inferenceCallCount;
    private bool _disposed;

    /// <summary>
    /// Number of actual model executions performed by this service instance. Cache hits do not
    /// increment it, which lets callers verify the cache (a fully cached scan leaves it flat).
    /// </summary>
    public int InferenceCallCount => Volatile.Read(ref _inferenceCallCount);

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
        var fileName = Path.GetFileName(modelPath) ?? string.Empty;
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

            // Warm the session cache from the previous runs' persisted results. Keys embed the
            // source file's last-write time, so entries for since-edited files simply never hit.
            _persistentStore = new AiResultStore(AiResultStore.DefaultPath, fileName);
            foreach (var (key, value) in _persistentStore.Load())
            {
                _resultCache[key] = value;
            }
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
    public string ExplainMethod(MethodInfo methodInfo, Action<string>? onPartial = null)
    {
        if (!IsReady)
        {
            return LoadError ?? "The explanation model is not available.";
        }

        return RunCached("explain", methodInfo, () =>
        {
            var prompt = BuildExplainMethodPrompt(methodInfo);
            return TruncateProse(RunInstruction(prompt, MaxTokensExplanation, onPartial), maxSentences: 3, maxWords: 80);
        });
    }

    /// <summary>
    /// Generates a brief developer-style description when no XML summary exists.
    /// </summary>
    public string GenerateBriefDescription(MethodInfo methodInfo, Action<string>? onPartial = null)
    {
        if (!IsReady)
        {
            return LoadError ?? "The explanation model is not available.";
        }

        return RunCached("brief", methodInfo, () =>
        {
            var source = GetMethodSourceSnippet(methodInfo, MaxSourceSnippetBrief);
            var userPrompt =
                $"Method: {methodInfo.Name}, Returns: {methodInfo.ReturnType}, " +
                $"Params: {string.Join(", ", methodInfo.Parameters)}, Source: {source}";

            var systemPrompt = "Write one sentence describing what this C++ or C# method actually does, " +
                               "naming its key logic (checks, loops, calculations, calls) from the source.";
            var languageSuffix = GetLanguageSystemSuffix(methodInfo);
            if (!string.IsNullOrEmpty(languageSuffix))
            {
                systemPrompt += " " + languageSuffix;
            }

            return TruncateProse(RunInstruction(userPrompt, MaxTokensBrief, systemPrompt, onPartial), maxSentences: 1, maxWords: 35);
        });
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
    /// Generates a 2-3 sentence description of a class's responsibility from its verified
    /// members and relationships. Cached per (file, class) for the session so revisiting a
    /// class costs nothing.
    /// </summary>
    public string GenerateClassSummary(ClassInfo classInfo, Action<string>? onPartial = null)
    {
        ArgumentNullException.ThrowIfNull(classInfo);

        if (!IsReady)
        {
            return LoadError ?? "The explanation model is not available.";
        }

        var key = BuildClassCacheKey("classsummary", classInfo);
        if (_resultCache.TryGetValue(key, out var hit))
        {
            return hit;
        }

        var systemPrompt = "You are a concise technical writer. In 2-3 sentences, describe this " +
                           "class's responsibility using only the listed members and relationships. " +
                           "Stay factual; do not invent behavior the members do not show.";
        var result = TruncateProse(
            RunInstruction(DescribeClassCompact(classInfo), MaxTokensExplanation, systemPrompt, onPartial),
            maxSentences: 3,
            maxWords: 80);

        // Small models sometimes tack markup fragments ("<", ">", "|", backticks) after the
        // final sentence; no legitimate sentence ends with them.
        result = result.TrimEnd().TrimEnd('>', '<', '|', '`', '#').TrimEnd();

        if (IsCleanOutput(result))
        {
            _resultCache[key] = result;
        }

        return result;
    }

    /// <summary>
    /// The verified facts a class summary may draw from: name, base types, dependencies, and
    /// member signatures (capped so a god class cannot blow the prompt budget).
    /// </summary>
    private static string DescribeClassCompact(ClassInfo classInfo)
    {
        var sb = new StringBuilder();
        sb.Append("Class: ").Append(classInfo.Name);

        if (!string.IsNullOrEmpty(classInfo.BaseClassName))
        {
            sb.Append(", Extends: ").Append(classInfo.BaseClassName);
        }

        if (classInfo.ImplementedInterfaces.Count > 0)
        {
            sb.Append(", Implements: ").Append(string.Join(", ", classInfo.ImplementedInterfaces));
        }

        if (classInfo.Dependencies.Count > 0)
        {
            sb.Append(", Uses: ").Append(string.Join(", ", classInfo.Dependencies.Take(8)));
        }

        sb.Append(". Methods: ");
        sb.Append(classInfo.Methods.Count == 0
            ? "none"
            : string.Join("; ", classInfo.Methods.Take(12).Select(m =>
                $"{m.ReturnType} {m.Name}({string.Join(", ", m.Parameters)})")));

        if (classInfo.Properties.Count > 0)
        {
            sb.Append(". Properties: ");
            sb.Append(string.Join(", ", classInfo.Properties.Take(8).Select(p => $"{p.Type} {p.Name}")));
        }

        return sb.ToString();
    }

    private static string BuildClassCacheKey(string operation, ClassInfo classInfo)
    {
        var path = classInfo.SourceFilePath ?? string.Empty;
        long lastWriteTicks = 0;
        try
        {
            if (path.Length > 0 && File.Exists(path))
            {
                lastWriteTicks = File.GetLastWriteTimeUtc(path).Ticks;
            }
        }
        catch
        {
            // A failed stat just weakens the key to (path, name); still session-safe.
        }

        var material = $"{operation}|{path}|{lastWriteTicks}|{classInfo.Name}|{classInfo.Methods.Count}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
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

        return RunCached("prepost", methodInfo, () =>
        {
            var prompt = BuildBulletPrompt(
                "List pre-conditions then post-conditions. Pre-conditions: only requirements the code enforces " +
                "via guard clauses in the source. Post-conditions: what the return value means, using the verified " +
                "return statements. Never invent checks absent from the source. " +
                "Output a line 'PRE:' followed by up to 3 '- text' bullets, then a line 'POST:' followed by up to " +
                "3 '- text' bullets. Emit both marker lines even when a section has no bullets.",
                DescribeMethodCompact(methodInfo),
                methodInfo);
            return TruncateSectionedBullets(RunInstruction(prompt, MaxTokensBullets), maxBulletsPerSection: 3, maxWordsPerBullet: 20);
        });
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

        return RunCached("design", methodInfo, () =>
        {
            var prompt = BuildBulletPrompt(
                "List up to 4 design constraints as '- ' bullets. Name the constructs the code actually uses " +
                "(recursion, lock, async/await, loops, LINQ), plus state, dependencies, and side effects. " +
                "Cover every verified fact that applies; claim nothing the code does not do.",
                DescribeMethodCompact(methodInfo),
                methodInfo);
            return TruncateBullets(RunInstruction(prompt, MaxTokensBullets), maxBullets: 4, maxWordsPerBullet: 20);
        });
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

        return RunCached("errors", methodInfo, () =>
        {
            var prompt = BuildBulletPrompt(
                "Only identify errors actually possible given the source code. " +
                "The verified Throws fact is authoritative: name each thrown exception and what triggers it, " +
                "and mention exceptions that are caught and handled internally. " +
                "Do not list generic errors unrelated to this method. " +
                "Only say there are no error conditions when the verified facts show no throw statements. Max 4 '- ' bullets.",
                DescribeMethodCompact(methodInfo),
                methodInfo);
            return TruncateBullets(RunInstruction(prompt, MaxTokensErrors), maxBullets: 4, maxWordsPerBullet: 22);
        });
    }

    /// <summary>
    /// Generates all five AI documentation sections for a method in a <b>single</b> model call
    /// with a structured output format, then splits the response. Replaces five sequential
    /// inference round-trips in bulk paths (Word export). Sections that come back empty fall
    /// back to their individual single-section calls. Results are cached under the same keys
    /// the individual methods use, so a later single-section request is a cache hit.
    /// </summary>
    public MethodAiDocumentation GenerateMethodDocumentation(MethodInfo methodInfo, CancellationToken cancellationToken = default)
    {
        if (!IsReady)
        {
            var unavailable = LoadError ?? "The explanation model is not available.";
            return new MethodAiDocumentation(unavailable, unavailable, unavailable, unavailable, unavailable, UsedAi: false);
        }

        // Fast path: everything already cached from earlier UI interactions or a prior export.
        if (TryGetCachedDocumentation(methodInfo, out var cached))
        {
            return cached;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var instruction = BuildMergedDocumentationPrompt(methodInfo);
        var response = RunInstruction(instruction, MaxTokensMergedDocumentation,
            "You are a concise technical writer. Fill in every requested section, keeping the exact '### ' headers. " +
            "Stay factual and never contradict the verified facts in the request.",
            cancellationToken);

        // The per-section fallbacks below each run their own (uncancellable) inference call;
        // don't start any of them for a request that has already been canceled.
        cancellationToken.ThrowIfCancellationRequested();

        var sections = SplitSections(response);

        var brief = PostProcessSection(sections, "BRIEF",
            raw => TruncateProse(raw, maxSentences: 1, maxWords: 35),
            () => GenerateBriefDescription(methodInfo));
        var prePost = PostProcessSection(sections, "CONDITIONS",
            raw => TruncateSectionedBullets(raw, maxBulletsPerSection: 3, maxWordsPerBullet: 20),
            () => GeneratePrePostConditions(methodInfo));
        var design = PostProcessSection(sections, "DESIGN",
            raw => TruncateBullets(raw, maxBullets: 4, maxWordsPerBullet: 20),
            () => GenerateDesignConstraints(methodInfo));
        var errors = PostProcessSection(sections, "ERRORS",
            raw => TruncateBullets(raw, maxBullets: 4, maxWordsPerBullet: 22),
            () => GenerateErrorAnalysis(methodInfo));
        var explanation = PostProcessSection(sections, "EXPLANATION",
            raw => TruncateProse(raw, maxSentences: 3, maxWords: 80),
            () => ExplainMethod(methodInfo));

        CacheSection("brief", methodInfo, brief);
        CacheSection("prepost", methodInfo, prePost);
        CacheSection("design", methodInfo, design);
        CacheSection("errors", methodInfo, errors);
        CacheSection("explain", methodInfo, explanation);

        // The model ran (IsReady above); treat the result as genuine AI output when the Brief
        // section came back as clean text rather than a bracketed error/unavailable message.
        return new MethodAiDocumentation(brief, prePost, design, errors, explanation, UsedAi: IsCleanOutput(brief));
    }

    private bool TryGetCachedDocumentation(MethodInfo methodInfo, out MethodAiDocumentation documentation)
    {
        documentation = default!;
        if (_resultCache.TryGetValue(BuildCacheKey("brief", methodInfo), out var brief) &&
            _resultCache.TryGetValue(BuildCacheKey("prepost", methodInfo), out var prePost) &&
            _resultCache.TryGetValue(BuildCacheKey("design", methodInfo), out var design) &&
            _resultCache.TryGetValue(BuildCacheKey("errors", methodInfo), out var errors) &&
            _resultCache.TryGetValue(BuildCacheKey("explain", methodInfo), out var explanation))
        {
            // Only clean output is ever cached, so a full cache hit is by definition genuine AI.
            documentation = new MethodAiDocumentation(brief, prePost, design, errors, explanation, UsedAi: true);
            return true;
        }

        return false;
    }

    private void CacheSection(string operation, MethodInfo methodInfo, string value)
    {
        if (IsCleanOutput(value))
        {
            _resultCache[BuildCacheKey(operation, methodInfo)] = value;
        }
    }

    private static string BuildMergedDocumentationPrompt(MethodInfo methodInfo)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Document this method in five sections. Output exactly these '### ' headers in this order, each followed by its content:");
        builder.AppendLine("### BRIEF");
        builder.AppendLine("One sentence describing what the method's code actually does — name its key logic; do not restate the Summary line.");
        builder.AppendLine("### CONDITIONS");
        builder.AppendLine("A line 'PRE:' followed by up to 3 '- ' bullets stating only requirements the code enforces via guard clauses, then a line 'POST:' followed by up to 3 '- ' bullets stating what the return value means, using the verified return statements. Emit both marker lines even when a section has no bullets. Never invent checks absent from the source.");
        builder.AppendLine("### DESIGN");
        builder.AppendLine("Up to 4 design '- ' bullets naming the constructs the code actually uses (recursion, lock, async/await, loops, LINQ), plus state, dependencies, side effects. Cover every verified fact that applies; claim nothing the code does not do.");
        builder.AppendLine("### ERRORS");
        builder.AppendLine("Up to 4 '- ' bullets consistent with the verified Throws fact: name each thrown exception and its trigger, note exceptions handled internally, or say none only when the code truly cannot fail.");
        builder.AppendLine("### EXPLANATION");
        builder.AppendLine("2-3 sentences summarizing what the method does and what it returns.");
        builder.AppendLine("Plain text only — no markdown bold, headings, or labels inside sections.");

        var languageSuffix = GetLanguageSystemSuffix(methodInfo);
        if (!string.IsNullOrEmpty(languageSuffix))
        {
            builder.AppendLine(languageSuffix);
        }

        builder.AppendLine();
        builder.Append(DescribeMethodCompact(methodInfo));
        return builder.ToString();
    }

    /// <summary>
    /// Splits a merged response into a name→content map on its <c>### NAME</c> headers.
    /// Tolerates missing sections and extra whitespace; matching is case-insensitive.
    /// </summary>
    private static Dictionary<string, string> SplitSections(string response)
    {
        var sections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? current = null;
        var content = new StringBuilder();

        foreach (var line in response.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r').Trim();
            if (trimmed.StartsWith("###", StringComparison.Ordinal))
            {
                if (current is not null)
                {
                    sections[current] = content.ToString().Trim();
                }

                current = trimmed.TrimStart('#', ' ').Trim().ToUpperInvariant();
                content.Clear();
            }
            else if (current is not null)
            {
                content.AppendLine(trimmed);
            }
        }

        if (current is not null)
        {
            sections[current] = content.ToString().Trim();
        }

        return sections;
    }

    private static string PostProcessSection(
        Dictionary<string, string> sections,
        string name,
        Func<string, string> shape,
        Func<string> fallback)
    {
        if (sections.TryGetValue(name, out var raw) && !string.IsNullOrWhiteSpace(raw))
        {
            var shaped = shape(raw);
            if (IsCleanOutput(shaped))
            {
                return shaped;
            }
        }

        return fallback();
    }

    /// <summary>
    /// Starts a multi-turn conversation about a method.
    /// </summary>
    public IMethodConversationSession StartMethodConversation(MethodInfo methodInfo, string initialExplanation)
    {
        return new MethodConversationSession(this, methodInfo, initialExplanation);
    }

    /// <summary>
    /// Runs a single instruction through a fresh context/executor and returns the full response
    /// text. Catches all failures and returns them as a message string so callers never crash.
    /// </summary>
    internal string RunInstruction(string instruction) =>
        RunInstruction(instruction, MaxTokensExplanation);

    internal string RunInstruction(string instruction, int maxTokens, Action<string>? onPartial = null) =>
        RunInstruction(instruction, maxTokens, DefaultSystemPrompt, onPartial);

    internal string RunInstruction(string instruction, int maxTokens, string systemPrompt, Action<string>? onPartial = null) =>
        RunInstruction(instruction, maxTokens, systemPrompt, CancellationToken.None, onPartial);

    internal string RunInstruction(
        string instruction,
        int maxTokens,
        string systemPrompt,
        CancellationToken cancellationToken,
        Action<string>? onPartial = null)
    {
        if (!IsReady || _weights is null || _modelParams is null)
        {
            return LoadError ?? "The explanation model is not available.";
        }

        try
        {
            return Task.Run(
                    () => RunInstructionAsync(instruction, maxTokens, systemPrompt, cancellationToken, onPartial),
                    cancellationToken)
                .GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // Cancellation is a caller decision, not an inference failure — let it propagate so
            // bulk paths (Word export) stop instead of embedding an error string in the document.
            throw;
        }
        catch (Exception ex)
        {
            return $"[Inference failed: {ex.Message}]";
        }
    }

    private async Task<string> RunInstructionAsync(
        string instruction,
        int maxTokens,
        string systemPrompt,
        CancellationToken cancellationToken,
        Action<string>? onPartial = null)
    {
        await _inferenceLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RunInstructionLockedAsync(instruction, maxTokens, systemPrompt, cancellationToken, onPartial).ConfigureAwait(false);
        }
        finally
        {
            _inferenceLock.Release();
        }
    }

    // ── Session result cache ─────────────────────────────────────────────────

    /// <summary>
    /// Runs <paramref name="run"/> unless an identical request (same operation, same method,
    /// unchanged source file) already succeeded this session, in which case the cached result is
    /// returned without touching the model. Only clean output is cached — bracketed
    /// error/unavailable strings are not, so the model gets retried once it becomes usable.
    /// </summary>
    private string RunCached(string operation, MethodInfo methodInfo, Func<string> run)
    {
        var key = BuildCacheKey(operation, methodInfo);
        if (_resultCache.TryGetValue(key, out var hit))
        {
            return hit;
        }

        var result = run();
        if (IsCleanOutput(result))
        {
            _resultCache[key] = result;
        }

        return result;
    }

    private static string BuildCacheKey(string operation, MethodInfo methodInfo)
    {
        var path = methodInfo.ParentClass?.SourceFilePath ?? string.Empty;
        long lastWriteTicks = 0;
        try
        {
            if (path.Length > 0 && File.Exists(path))
            {
                lastWriteTicks = File.GetLastWriteTimeUtc(path).Ticks;
            }
        }
        catch
        {
            // A failed stat just weakens the key to (path, signature); still session-safe.
        }

        var signature =
            $"{methodInfo.ParentClass?.Name}|{methodInfo.ReturnType} {methodInfo.Name}({string.Join(",", methodInfo.Parameters)})";
        var material = $"{operation}|{path}|{lastWriteTicks}|{signature}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    private static bool IsCleanOutput(string result) =>
        !string.IsNullOrWhiteSpace(result) && !result.StartsWith('[');

    private async Task<string> RunInstructionLockedAsync(
        string instruction,
        int maxTokens,
        string systemPrompt,
        CancellationToken cancellationToken,
        Action<string>? onPartial = null)
    {
        Interlocked.Increment(ref _inferenceCallCount);

        // Analyzed source code is untrusted input: strip chat-template special tokens and
        // control characters before it is embedded in the prompt, so a file containing e.g.
        // "<|im_end|>" cannot break out of the template or truncate the response.
        instruction = SanitizeForPrompt(instruction);
        systemPrompt = SanitizeForPrompt(systemPrompt);

        // One context for the service lifetime; KV cache cleared per call so prompts stay
        // isolated without re-allocating the context's native buffers every time.
        var context = _context ??= _weights!.CreateContext(_modelParams!);
        context.NativeHandle.MemoryClear(true);

        // Hard input budget: prompts are structurally small (~100-200 tokens), but a method
        // with a pathological signature/doc could exceed it. Trim from the end of the
        // instruction (the source snippet lives there) rather than failing.
        var promptTokens = context.Tokenize(systemPrompt + "\n" + instruction, addBos: false, special: true).Length;
        if (promptTokens > MaxInputTokens)
        {
            var keep = Math.Max(64, (int)(instruction.Length * ((double)MaxInputTokens / promptTokens)) - 32);
            if (keep < instruction.Length)
            {
                System.Diagnostics.Trace.TraceWarning(
                    $"[JBU CodeLens] Prompt truncated: {promptTokens} tokens exceeded the {MaxInputTokens}-token input budget; " +
                    $"kept the first {keep} of {instruction.Length} instruction characters.");
                instruction = instruction[..keep] + "…";
            }
        }

        var inferenceParams = new InferenceParams
        {
            MaxTokens = maxTokens,
            SamplingPipeline = new DefaultSamplingPipeline
            {
                Temperature = 0.1f,
            },
        };

        var builder = new StringBuilder();

        // Live-streams the accumulated text to the caller as the model produces it. Markdown
        // emphasis is stripped like the final text, and a short trailing "<…" fragment is held
        // back so a partially generated stop marker (e.g. "<|im_end") never flashes on screen.
        void ReportPartial()
        {
            if (onPartial is null)
            {
                return;
            }

            var partial = StripMarkdownEmphasis(builder.ToString()).TrimStart();
            var angle = partial.LastIndexOf('<');
            if (angle >= 0 && partial.Length - angle <= 12)
            {
                partial = partial[..angle];
            }

            if (partial.Length > 0)
            {
                onPartial(partial);
            }
        }

        if (_usesPhiTemplate)
        {
            inferenceParams.AntiPrompts = new[] { "<|end|>", "<|user|>", "<|system|>" };
            var prompt = FormatPhiPrompt(systemPrompt, instruction);
            var executor = new InstructExecutor(context, string.Empty, string.Empty);
            await foreach (var token in executor.InferAsync(prompt, inferenceParams, cancellationToken).ConfigureAwait(false))
            {
                builder.Append(token);
                ReportPartial();
            }
        }
        else if (_usesChatMlTemplate)
        {
            inferenceParams.AntiPrompts = new[] { "<|im_end|>", "<|im_start|>" };
            var prompt = FormatChatMlPrompt(systemPrompt, instruction);
            var executor = new InstructExecutor(context, string.Empty, string.Empty);
            await foreach (var token in executor.InferAsync(prompt, inferenceParams, cancellationToken).ConfigureAwait(false))
            {
                builder.Append(token);
                ReportPartial();
            }
        }
        else
        {
            // CodeLlama-Instruct expects the "[INST] ... [/INST]" wrapper.
            inferenceParams.AntiPrompts = new[] { "[INST]", "</s>" };
            var executor = new InstructExecutor(context, "[INST] ", " [/INST]");
            await foreach (var token in executor.InferAsync(instruction, inferenceParams, cancellationToken).ConfigureAwait(false))
            {
                builder.Append(token);
                ReportPartial();
            }
        }

        return CleanResponse(builder.ToString(), _usesPhiTemplate, _usesChatMlTemplate);
    }

    /// <summary>
    /// Removes chat-template special-token sequences (<c>&lt;|…|&gt;</c>) and non-printable
    /// control characters (keeping newlines and tabs) from text destined for a prompt.
    /// </summary>
    internal static string SanitizeForPrompt(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var cleaned = SafeRegex.Replace(text, @"<\|[^|>]{0,32}\|>", " ");
        return SafeRegex.Replace(cleaned, @"[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]", " ");
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
    /// The verified-facts block sits before the source snippet because prompt overflow is
    /// trimmed from the end of the instruction: the snippet may be cut, the facts never are.
    /// </summary>
    internal static string DescribeMethodCompact(MethodInfo methodInfo)
    {
        var builder = new StringBuilder();
        builder.AppendLine(CultureInfo.InvariantCulture, $"Method: {methodInfo.ReturnType} {methodInfo.Name}({string.Join(", ", methodInfo.Parameters)})");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Class: {methodInfo.ParentClass?.Name ?? "unknown"}");

        if (!string.IsNullOrEmpty(methodInfo.XmlSummary))
            builder.AppendLine(CultureInfo.InvariantCulture, $"Summary: {methodInfo.XmlSummary}");

        if (methodInfo.OperationalLimits.Count > 0)
            builder.AppendLine(CultureInfo.InvariantCulture, $"Guards: {string.Join("; ", methodInfo.OperationalLimits.Take(3))}");

        builder.Append(BuildVerifiedFacts(methodInfo));

        var source = GetMethodSourceSnippet(methodInfo);
        if (!string.IsNullOrEmpty(source))
            builder.AppendLine(CultureInfo.InvariantCulture, $"Source: {source}");

        return builder.ToString();
    }

    /// <summary>
    /// Deterministic facts derived from the parse tree and the full (untruncated) method body,
    /// injected into every prompt so the model can neither miss structural patterns (recursion,
    /// locking, async, swallowed exceptions, state mutation) nor contradict the code — the two
    /// failure modes with the model-generated sections.
    /// </summary>
    internal static string BuildVerifiedFacts(MethodInfo methodInfo)
    {
        methodInfo.XmlDocTags.TryGetValue("sourceCode", out var source);
        source = source?.Trim() ?? string.Empty;

        var facts = new List<string>();

        // The C++ parser only learns exceptions from doc comments, so an empty list there does
        // not prove the body is throw-free — fall back to scanning the body text.
        if (methodInfo.ThrownExceptions.Count > 0)
        {
            facts.Add($"Throws: {string.Join(", ", methodInfo.ThrownExceptions)}.");
        }
        else if (source.Length > 0)
        {
            facts.Add(SourcePatternHelpers.ContainsThrow(source)
                ? "Contains throw statements."
                : "Contains no throw statements.");
        }

        if (IsRecursive(methodInfo, source))
        {
            facts.Add("Recursive: the method calls itself.");
        }

        if (source.Length > 0)
        {
            if (source.Contains("await ", StringComparison.Ordinal))
            {
                facts.Add("Asynchronous: awaits one or more operations.");
            }

            if (SafeRegex.IsMatch(source, @"\block\s*\(") || source.Contains("std::lock_guard", StringComparison.Ordinal))
            {
                facts.Add("Uses a lock for thread safety.");
            }

            if (SafeRegex.IsMatch(source, @"\bcatch\b"))
            {
                facts.Add(SourcePatternHelpers.HasCatchWithoutRethrow(source)
                    ? "Catches exceptions internally without rethrowing; callers get a normal return value instead of the exception."
                    : "Contains try/catch handling.");

                if (SafeRegex.IsMatch(source, @"\bfinally\b"))
                {
                    facts.Add("Has a finally block that always runs.");
                }
            }

            var mutatedFields = GetMutatedFields(methodInfo, source);
            if (mutatedFields.Count > 0)
            {
                facts.Add($"Modifies class state: {string.Join(", ", mutatedFields)}.");
            }
            else if (!HasAnyMutation(source))
            {
                facts.Add("Modifies no state: the body contains no assignments or mutating calls.");
            }

            var returnStatements = ExtractReturnStatements(source);
            if (returnStatements.Count > 0)
            {
                facts.Add($"Return statements: {string.Join(" ; ", returnStatements)}");
            }
        }

        var builder = new StringBuilder();
        builder.AppendLine("Verified facts from code analysis (authoritative — never contradict them):");
        foreach (var fact in facts)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"- {fact}");
        }

        return builder.ToString();
    }

    private static bool IsRecursive(MethodInfo methodInfo, string source)
    {
        var name = methodInfo.Name;
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        if (methodInfo.CalledMethodNames.Any(call =>
                call.Equals(name, StringComparison.Ordinal) ||
                call.EndsWith("." + name, StringComparison.Ordinal)))
        {
            return true;
        }

        if (source.Length == 0)
        {
            return false;
        }

        // The C# parser stores the body only, so any self-call matches; the C++ parser stores
        // the whole definition, whose signature contributes one non-call occurrence.
        var selfCalls = SafeRegex.Matches(source, $@"\b{Regex.Escape(name)}\s*\(").Count;
        var isCSharp = LanguageFileExtensions.IsCSharpFile(methodInfo.ParentClass?.SourceFilePath ?? string.Empty);
        return isCSharp ? selfCalls >= 1 : selfCalls >= 2;
    }

    /// <summary>
    /// True when the body performs any assignment (plain or compound), increment/decrement, or
    /// mutating collection call. Local declarations count too, so this only clears genuinely
    /// expression-shaped bodies (pure LINQ chains, switch expressions, recursion) — a
    /// deliberately conservative bar for claiming "modifies no state".
    /// </summary>
    private static bool HasAnyMutation(string source) =>
        SafeRegex.IsMatch(source, @"[+\-*/%|&^]=(?!=)") ||
        SafeRegex.IsMatch(source, @"(?<![=!<>+\-*/%|&^])=(?!=|>)") ||
        SafeRegex.IsMatch(source, @"\+\+|--") ||
        SafeRegex.IsMatch(source,
            @"\.\s*(Add|AddRange|Remove|RemoveAll|RemoveAt|Clear|Insert|Push|Pop|Enqueue|Dequeue|push_back|pop_back|erase|clear|insert)\s*\(");

    private static List<string> GetMutatedFields(MethodInfo methodInfo, string source)
    {
        var mutated = new List<string>();
        foreach (var field in methodInfo.ParentClass?.Fields ?? [])
        {
            if (string.IsNullOrEmpty(field.Name))
            {
                continue;
            }

            if (SourcePatternHelpers.IsWrittenInSource(source, field.Name) ||
                SafeRegex.IsMatch(source,
                    $@"\b{Regex.Escape(field.Name)}\s*\.\s*(Add|AddRange|Remove|RemoveAll|RemoveAt|Clear|Insert|Push|Pop|Enqueue|Dequeue|push_back|pop_back|erase|clear|insert)\s*\("))
            {
                mutated.Add(field.Name);
            }
        }

        return mutated;
    }

    private static List<string> ExtractReturnStatements(string source)
    {
        var results = new List<string>();
        foreach (Match match in SafeRegex.Matches(source, @"\breturn\b([^;]*);"))
        {
            var expression = SafeRegex.Replace(match.Groups[1].Value, @"\s+", " ").Trim();
            if (expression.Length > 70)
            {
                expression = expression[..70] + "…";
            }

            var formatted = expression.Length == 0 ? "return;" : $"return {expression};";
            if (!results.Contains(formatted))
            {
                results.Add(formatted);
                if (results.Count == 4)
                {
                    break;
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Produces a compact, structured textual description of a method for use inside prompts.
    /// </summary>
    internal static string DescribeMethod(MethodInfo methodInfo)
    {
        var builder = new StringBuilder();
        builder.AppendLine(CultureInfo.InvariantCulture, $"Method name: {methodInfo.Name}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Return type: {methodInfo.ReturnType}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Access: {methodInfo.AccessModifier}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Parent class: {methodInfo.ParentClass?.Name ?? "unknown"}");
        builder.AppendLine(
            CultureInfo.InvariantCulture,
            $"Parameters: {(methodInfo.Parameters.Count > 0 ? string.Join(", ", methodInfo.Parameters) : "none")}");

        if (!string.IsNullOrEmpty(methodInfo.XmlSummary))
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"Existing doc summary: {methodInfo.XmlSummary}");
        }

        if (methodInfo.ThrownExceptions.Count > 0)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"Thrown exceptions: {string.Join(", ", methodInfo.ThrownExceptions)}");
        }

        if (methodInfo.OperationalLimits.Count > 0)
        {
            builder.AppendLine("Operational limits from code:");
            foreach (var limit in methodInfo.OperationalLimits)
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"  - {limit}");
            }
        }

        var globals = methodInfo.ParentClass?.Fields ?? [];
        if (globals.Count > 0)
        {
            builder.AppendLine("Class fields:");
            foreach (var field in globals)
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"  {field.AccessModifier} {field.Type} {field.Name}");
            }
        }

        if (methodInfo.LocalVariables.Count > 0)
        {
            builder.AppendLine("Local variables:");
            foreach (var local in methodInfo.LocalVariables)
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"  {local.Type} {local.Name}");
            }
        }

        var source = GetMethodSourceSnippet(methodInfo);
        if (!string.IsNullOrEmpty(source))
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"Source: {source}");
        }

        return builder.ToString();
    }

    /// <summary>
    /// Trims whitespace and strips any trailing instruction/end markers that leak into the output.
    /// </summary>
    private static string CleanResponse(string text, bool usesPhiTemplate, bool usesChatMlTemplate)
    {
        var cleaned = text.Trim();

        string[] markers;
        if (usesPhiTemplate)
        {
            markers = ["<|end|>", "<|user|>", "<|system|>", "<|assistant|>"];
        }
        else if (usesChatMlTemplate)
        {
            markers = ["<|im_end|>", "<|im_start|>"];
        }
        else
        {
            markers = ["[INST]", "</s>"];
        }

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

  /// <summary>
  /// Removes markdown emphasis the model leaks into plain-text output (e.g. 'State**:'),
  /// which otherwise survives into the rendered documentation verbatim.
  /// </summary>
  /// <summary>
  /// True for a line carrying no information — a bare quote marker, a dangling list number, or
  /// stray punctuation. The model emits these when generation stops mid-item, and without this
  /// they reach the panel as an empty bullet.
  /// </summary>
  private static bool IsDebrisLine(string line)
  {
    var t = line.Trim();
    if (t.Length == 0) return true;
    return t is ">" or "-" or "*" or "•" or "#" || SafeRegex.IsMatch(t, @"^\d+[.)]?$");
  }

  internal static string StripMarkdownEmphasis(string text) =>
    text.Replace("**", string.Empty, StringComparison.Ordinal).Replace("__", string.Empty, StringComparison.Ordinal);

  private static string TruncateProse(string text, int maxSentences, int maxWords)
  {
    if (text.StartsWith('[')) return text;

    text = StripMarkdownEmphasis(text);
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

  /// <summary>
  /// Truncates pre/post-condition output while preserving its <c>PRE:</c>/<c>POST:</c> markers, so
  /// the UI and the Word export can present each group under its own label. Each section is capped
  /// independently — a model that emits six preconditions and no postconditions loses the surplus
  /// preconditions rather than crowding out the postconditions.
  /// Falls back to a flat list when the model ignored the markers.
  /// </summary>
  private static string TruncateSectionedBullets(string text, int maxBulletsPerSection, int maxWordsPerBullet)
  {
    if (text.StartsWith('[')) return text;

    var groups = PrePostConditionText.Split(text);
    if (!groups.IsGrouped)
    {
      return TruncateBullets(text, maxBulletsPerSection * 2, maxWordsPerBullet);
    }

    var builder = new StringBuilder();
    AppendSection(PrePostConditionText.PreMarker, groups.Preconditions);
    AppendSection(PrePostConditionText.PostMarker, groups.Postconditions);
    return builder.ToString().TrimEnd();

    void AppendSection(string marker, IReadOnlyList<string> bullets)
    {
      builder.AppendLine(marker);
      foreach (var bullet in bullets.Where(b => !IsDebrisLine(b)).Take(maxBulletsPerSection))
      {
        var words = bullet.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var line = words.Length > maxWordsPerBullet
          ? string.Join(' ', words.Take(maxWordsPerBullet)) + "…"
          : bullet;
        builder.AppendLine(CultureInfo.InvariantCulture, $"- {StripMarkdownEmphasis(line)}");
      }
    }
  }

  private static string TruncateBullets(string text, int maxBullets, int maxWordsPerBullet)
  {
    if (text.StartsWith('[')) return text;

    var lines = new List<string>();
    foreach (var rawLine in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
      var line = StripMarkdownEmphasis(rawLine).TrimStart('-', '•', '*', '#', ' ');
      if (string.IsNullOrWhiteSpace(line) || IsDebrisLine(line)) continue;

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

        // Persist this session's inference results so the next run starts warm.
        _persistentStore?.Save(_resultCache);

        _context?.Dispose();
        _weights?.Dispose();
        _inferenceLock.Dispose();
        _disposed = true;
    }
}

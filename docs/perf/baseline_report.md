# JBU.CodeLens — Performance Baseline Report (Section 1)

**Date:** 2026-07-11
**Machine:** Intel i5-1145G7 (4C/8T), 16 GB RAM, Windows 11 Pro
**Model:** qwen2.5-coder-1.5b-instruct **Q4_K_M** (1.04 GiB, CPU-only, 7 inference threads)
**Harness:** Stopwatch console harness (temporary, outside the repo) over synthetic projects:
50 C# files (50 classes / 200 methods), 20 C++ files (40 classes / 80 methods), and a
15-file mixed project (10 C# + 5 C++). Warm numbers are runs 2-3 after JIT/assembly load.

## Measurements

| Pipeline | Operation | Time (ms) | Tokens (in/out) | Finding |
|---|---|---:|---|---|
| Roslyn | Parse 50 C# files, cold (run 1) | 1,648 | — | Cold cost is JIT + Roslyn assembly load, not parsing |
| Roslyn | Parse 50 C# files, warm (runs 2-3) | 28-29 | — | ≈0.6 ms/file — Roslyn syntax parsing is **not** a bottleneck |
| Clang | Parse 20 C++ files (runs 1-3) | 7,657-8,434 | — | ≈385 ms/file — **the dominant parse cost**; fully sequential today |
| SCIDE | Full pipeline, 50-file C# project (warm) | 299-337 | — | Deterministic analyzers + graph add ~270 ms over raw parsing |
| SCIDE | Full pipeline, mixed 15-file project (warm) | 1,998-2,135 | — | Dominated by the 5 C++ files (~5×385 ms) |
| AI | Model load (weights) | 1,780 | — | One-time cost at app start, already off the UI thread |
| AI | Brief description (typical 15-line method) | 3,230 | 103 / 32 | UI request→display latency ≈ this + ~1 dispatcher hop |
| AI | Brief description, repeat same method | 3,647 | 103 / 32 | **No service-level cache** — identical input re-runs full inference |
| AI | Full explanation | 5,641 | 163 / 85 | Separate model call from the brief description |
| AI | Pre & post conditions | 7,475 | 170 / 97 | Separate call |
| AI | Design constraints | 6,115 | 170 / 58 | Separate call |
| AI | Error analysis | 8,493 | 186 / 47 | Separate call |
| AI | Word export, AI mode, per method | ~31,000 (derived) | ~790 total | **5 sequential model calls per method** (brief + pre/post + design + errors + explanation) |
| Memory | Working set after parse benchmarks (post-GC) | 119 MB | — | Includes libclang native module; no growth across repeated runs |
| Memory | Working set during AI benchmarks | 1,702 MB | — | Weights (~1.1 GB mapped) + per-call KV/compute buffers |

## Pipeline audit findings

### 1. Roslyn
- **No `CSharpCompilation.Create` anywhere** — the parser already uses `CSharpSyntaxTree.ParseText`
  (pure syntax, no compilation, no references). The Section 2 instruction to remove compilation
  calls is already satisfied; nothing to convert.
- **No Roslyn workspaces are used** — no repeated workspace reloads exist.
- **No blocking `.Result`/`.Wait()` on Roslyn APIs** — all Roslyn calls are the synchronous
  syntax APIs (CPU-bound by design). The single sync-over-async site in the codebase is
  `ExplanationService.RunInstruction` (a deliberate sync facade always invoked from worker threads).
- **No eager symbol resolution** — no `SemanticModel` is ever requested.
- Real inefficiencies found instead:
  - `File.ReadAllText` (sync) per file, sequential single-threaded file loop.
  - **8 separate `DescendantNodes()` walks per method** (calls, complexity, thrown×2,
    locals×2, guard-clause limits×2) where one walk suffices.
  - Each scan re-parses every file even when unchanged since the previous scan.

### 2. Clang / C++
- **Exactly one `CXTranslationUnit` per file** already — created in `Parse`, used for all
  queries on that file, disposed in `finally`. No per-function TU creation exists.
- **All `CXString`s are disposed** — every P/Invoke returning `CXString` funnels through
  `MarshalCxString`, which calls `clang_disposeString` in a `finally`. Verified at all 6 call sites.
- **No unsaved-file buffers** — `clang_parseTranslationUnit` is passed `IntPtr.Zero` unsaved
  files and reads from disk (already index-from-file).
- Real inefficiencies found instead:
  - ~385 ms/file, **fully sequential**. A `CXIndex` is created per file (which is what makes
    parallelization safe — libclang parsing is thread-safe across separate indexes).
  - Sync `File.ReadAllText` for the source-extraction buffer.

### 3. AI inference
- **Token budget:** output budgets are enforced per call type (50-400 `MaxTokens`); input is
  bounded only indirectly via source-snippet truncation (300-400 chars). Measured prompts are
  103-186 tokens — all comfortably under the 512-token input target already.
- **Context reset:** a fresh `LLamaContext` (56 MB KV + 302 MB compute reservation) is created
  and disposed **per call** — confirmed in llama.cpp logs (`constructing llama_context` before
  every heading). KV state therefore never leaks between prompts, but each call pays context
  setup. Deliberate design (documented in source) to avoid KV pollution; Section 2 revisits it.
- **SemaphoreSlim / UI freezes:** all UI call sites wrap inference in `Task.Run`; the semaphore
  queues background threads only. No UI-thread blocking found.
- **Average per heading ≈ 6.2 s (range 3.2-8.5 s) — exceeds the 2 s/heading threshold.**
  See recommendation below.

### 4. Documentation heading generation
- The six method-detail headings are: Inputs/Outputs, Brief Description, Local & Global
  Variables, Pre & Post Conditions, Design Requirements, Errors/Exceptions. Three are
  deterministic (no model call); AI contributes to Brief Description (auto), Design and
  Errors (on demand), plus the separate AI Explanation card.
- **Brief description and full explanation are two separate model calls**, and the AI Word
  export makes **five** calls per method (~31 s/method). Class-level docs use the XML summary
  (no model call) — only method-level headings hit the model.
- **Cache-before-notify:** the UI writes `CachedAiBriefDescription` in the same dispatcher
  callback that updates the text block (no await between), so cache and notification are
  effectively atomic. However the cache lives on the transient `MethodInfo` object — a rescan
  discards it, and nothing caches at the service level (see repeat-brief row above).

## Model speed flag

Average heading latency (6.2 s) exceeds the 2 s target on this machine (4-core CPU-only).
Recommendation, in order:
1. Keep qwen2.5-coder-1.5b but switch to **Q4_K_S** or **IQ3_M** quant (~15-25% faster
   token generation at minor quality cost), **and**
2. Apply the Section 2 changes that reduce call *count* (merged structured call: 5→1 per
   method for export; service-level result cache: repeat views → 0 calls), which matter more
   than per-call speed here, since prompt sizes are already small.
3. `phi-3-mini-4k-instruct Q4_K_M` (3.8 B params) is a *quality* upgrade but ~2.5× slower per
   token on this CPU — it would move away from the 2 s target, not toward it. Not recommended
   for this machine.

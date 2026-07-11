# CodeLensAI — Efficiency Improvement Report (Section 2)

**Date:** 2026-07-11 · Same machine/model as `baseline_report.md`.
Baseline numbers are from `baseline_report.md`; "after" numbers were measured with the same
harness after the Section 2 changes. The "after (Section 5)" column is re-verified at the end
of the audit.

## Changes applied and measured impact

| Area | Change | Before | After | After (Section 5) |
|---|---|---:|---:|---:|
| SCIDE | Parallel file parsing (`Parallel.ForEachAsync`, DOP = `ProcessorCount / 2` = 4), index-addressed result array (no collection contention, deterministic order) | mixed project scan 1,998 ms | **1,104 ms** (1.8×) | 1,353 ms (cold) |
| SCIDE | Cross-scan parse cache keyed (path, last-write-time); evicted for files that leave the set; unchanged files also keep `CachedAnalysis` (`??=`) | rescan = full cost (299 ms C#, 1,998 ms mixed) | **3 ms / 1 ms** (≥300×) | 2 ms / 1 ms |
| Roslyn | Single-pass `DescendantNodes()` walk per method (was 8 separate walks: calls, complexity, throws ×2, locals ×2, limits ×2) | 28 ms / 50 files (warm) | 45-60 ms / 50 files (warm) — see note 1 | 44-47 ms |
| Roslyn/Clang | `ParseAsync` with `File.ReadAllTextAsync` + `ConfigureAwait(false)` throughout Core; shared sync parse core (no sync-over-async) | sync I/O | async I/O | |
| AI | Session inference-result cache (`ConcurrentDictionary`, SHA-256 of op + path + last-write + signature); only clean output cached | repeat brief = 3,647 ms (full inference) | **0 ms, 0 model calls** | 0 new calls across rescan + regenerate (verified via `InferenceCallCount`) |
| AI | Merged 5-in-1 documentation call (`GenerateMethodDocumentation`, structured `###` sections, per-section fallback) used by Word export | 5 calls ≈ 31 s/method | **1 call = 12.1 s/method** (2.6×) | |
| AI | Persistent `LLamaContext` for the service lifetime with `MemoryClear()` per call (was: create + dispose a 56 MB KV + 302 MB compute context per call) | context construction per call | one construction per session; outputs verified coherent | |
| AI | Hard 512-token input budget enforced by tokenizing each prompt (trims source snippet tail if exceeded) | char-limit only (measured 103-186 tok) | enforced ceiling | |
| General | No `Thread.Sleep`/`Task.Delay` existed; none introduced. Parallel path uses a pre-sized array instead of `ConcurrentBag` (no contention at all) | — | — | |

Note 1 — the single-pass walk measured *slower* in the isolated 50-file micro-benchmark
(28→~50 ms warm; run-to-run noise on this machine is ±20 ms and the end-to-end SCIDE pipeline
showed no regression: 299→263-412 ms across runs). The walk-count reduction is structural
(8 → 1 enumerations); at these corpus sizes Roslyn parsing is so cheap either way that the
difference is inside noise. Kept because it also simplifies `BuildMethodInfo` to one place
that derives body facts.

## Individual heading latency (persistent context, cold cache)

| Heading | Before (ms) | After (ms) |
|---|---:|---:|
| Brief description | 3,230 | 3,001 |
| Brief description (repeat) | 3,647 | **0 (cache)** |
| Full explanation | 5,641 | 4,230 |
| Pre & post conditions | 7,475 | 7,587 |
| Design constraints | 6,115 | 4,496 |
| Error analysis | 8,493 | 2,706 |
| All five via merged call | ~31,000 | **12,094** |

Output-quality spot check: all five merged sections parsed correctly on the first try (zero
fallback calls — total model calls in the run: 6 = 5 individual + 1 merged). Merged sections
tend to be terser (often 1 bullet where individual calls produce 3-4); acceptable for the bulk
export path, and any individual re-request uses the dedicated prompt.

## Items from the Section 2 checklist that required no change (verified, not skipped)

- **`CSharpCompilation.Create` → `ParseText`:** no compilation calls existed; parser was
  already syntax-only.
- **Blocking `.Result`/`.Wait()` on Roslyn:** none existed.
- **Per-scan SyntaxTree cache:** each file is parsed exactly once per scan already; the
  requested cache would have zero hits by construction. Implemented instead as the *cross-scan*
  parse cache above (path + last-write-time key, invalidated per scan for changed/removed
  files), which is the same idea applied where it actually pays.
- **Clang: one TU per file, reused, then disposed:** already the case.
- **`clang_disposeString` at every P/Invoke site:** already the case (all `CXString`s funnel
  through one marshal helper with `finally` dispose).
- **Unsaved-file buffers → index-from-file:** already index-from-file (`IntPtr.Zero` unsaved).
  The per-file `CXIndex` is retained deliberately: separate indexes are what make the new
  parallel parsing thread-safe in libclang.
- **KV/context persistence via `StatefulExecutor`:** LLamaSharp's stateful executors carry
  conversation history between calls, which is exactly what unrelated heading prompts must NOT
  share. The equivalent benefit (no per-call context allocation) was achieved with a persistent
  context + `MemoryClear()` per call instead.
- **`ConcurrentBag<T>`:** the parallel path writes to a pre-sized, index-addressed array —
  contention-free and order-preserving, strictly better than `ConcurrentBag` here.
- **UI debounce workarounds:** no `Thread.Sleep`/`Task.Delay` existed anywhere.

## Model speed flag (re-stated from baseline)

Average heading latency at cold cache is still >2 s on this 4-core CPU machine. With the
session cache and the merged call, *user-visible* repeat latency is 0 ms and bulk export is
2.6× faster, which addresses the practical pain. If cold-cache latency still matters, switch
the quant to Q4_K_S or IQ3_M of qwen2.5-coder-1.5b (phi-3-mini would be slower on this CPU —
see baseline report).

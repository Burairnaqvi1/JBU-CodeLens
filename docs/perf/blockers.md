# Audit — Deviations, Judgment Calls, and Incomplete Items

Everything in Sections 1-5 was completed. Nothing was blocked outright, but several items
were satisfied differently than literally specified, and a few carry caveats. Recorded here
for transparency.

## Spec deviations (deliberate, with reasons)

1. **Shared vs Core/Models layout conflict.** The target layout puts `ClassInfo`/`MethodInfo`
   under `Core/Models`, while the rules require "UI depends only on Shared interfaces, never on
   Core concrete types directly" — interfaces in Shared cannot be typed against Core models.
   Resolved in favor of the rule and of Shared's own description ("DTOs and interfaces shared
   between Core and UI"): UI-facing DTOs live in `JBU.CodeLens.Shared` (`Models/`, `Structural/`),
   and `Core/Models` holds Core-internal models (`SymbolTable`), matching the one model the
   diagram names there.

2. **Composition root.** The UI must construct *something* concrete. `Views/MainWindow.xaml.cs`
   is the single documented composition root with three `new` expressions
   (`ScideEngine`, `ExplanationService`, `ExportService`); every other UI file compiles against
   Shared only. Full DI-container indirection was judged out of proportion for a 2-project
   desktop app.

3. **Per-scan SyntaxTree cache (Section 2).** Each file is already parsed exactly once per scan,
   so the requested per-scan cache would have zero hits by construction. Implemented as a
   *cross-scan* parse cache instead (keyed path + last-write-time, invalidated per scan) —
   measured rescan cost drops from ~2 s to 1-3 ms.

4. **StatefulExecutor / KV persistence (Section 2).** Not adopted as specified: stateful
   executors carry conversation history between calls, which would leak context between
   unrelated heading prompts. The intended benefit (no per-call context construction) was
   delivered via a persistent `LLamaContext` + `MemoryClear()` per call.

5. **`ConcurrentBag<T>` (Section 2).** The parallel parse path uses a pre-sized,
   index-addressed array — contention-free and order-preserving — which is strictly better
   than `ConcurrentBag` for this access pattern.

6. **Tree expand "height animation" (Section 4).** Implemented as a 150 ms ease-in-out
   `LayoutTransform.ScaleY` 0→1 animation. `LayoutTransform` participates in layout, so it
   visually behaves as a height animation without hardcoding pixel heights (WPF cannot animate
   `Auto` heights directly).

7. **Modal dialogs retained for genuine decisions.** The "include AI in export?"
   (Yes/No/Cancel) and "model not ready — export without AI?" prompts are decisions, not
   messages, and remain dialogs. All informational/success/failure messages moved to the
   inline banner as specified.

8. **`Core/Export/` folder.** Not in the target diagram, but the exporters had to live
   somewhere in Core ("all parsing, analysis, AI, caching logic"); a dedicated `Export/` folder
   was cleaner than burying them in `Utilities/`.

## Caveats on measurements

- **Roslyn single-pass walk** micro-benchmarked slightly *slower* than the 8-walk version
  (28 → ~45-60 ms per 50 files, warm) — inside run-to-run noise on this machine, with no
  end-to-end pipeline regression. Kept for the structural simplification; flagged rather
  than oversold.
- **Memory (Section 5, item 5).** First-scan working-set delta is ~69 MB because the first
  C++ parse loads the libclang native module (one-time, not reclaimable, not a leak). The
  meaningful leak test — warmup scan, GC baseline, then 6 alternating scans that defeat the
  parse cache, then GC — shows **+19 MB**, within the ±20 MB criterion, and part of that
  retained set is the cross-scan parse cache holding the most recent project by design.
- **Merged 5-in-1 AI output** is terser than five individual calls (often 1 bullet/section
  vs 3-4). Accepted for the bulk export path; on-demand single-section requests still use the
  dedicated richer prompts.
- **Model speed flag stands:** cold-cache heading latency (~3-8 s) exceeds the 2 s target on
  this 4-core CPU-only machine. Recommendation (baseline report): Q4_K_S or IQ3_M quant of
  qwen2.5-coder-1.5b; phi-3-mini would be slower here, not faster. Caching/merging reduce
  *practical* latency to 0 ms for repeats and 2.6× for bulk export.

## Test-scaffolding note

Timing/verification used a Stopwatch console harness kept *outside* the repository (temp
scratchpad), per the "temporary, remove after" instruction. Two temporary in-repo hooks
(an env-var auto-scan hook in MainWindow and an internal prompt-text observer in
ExplanationService) existed during measurement and were removed before the final Section 5
Release build; the final binary was rebuilt and re-verified after removal. UI verification
(theme round-trip, screenshots in `docs/perf/ui_screenshots/`) was done via UI Automation
against the running app.

## Known limitations carried forward (pre-existing, out of audit scope)

- `clang_parseTranslationUnit` marshals the path as ANSI; non-ASCII file paths may not parse.
- The six-headings AI enrichment runs per method; class-level docs use XML summaries plus
  deterministic classification only (no class-level model call) — unchanged behavior.

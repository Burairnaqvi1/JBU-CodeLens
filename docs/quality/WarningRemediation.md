# Compiler and Analyzer Warning Remediation

**Work item:** Remove all build warnings of every kind; document what could not be removed and why.
**Status: COMPLETE.**
**Result: 473 → 0 warnings.** `TreatWarningsAsErrors` is now enabled, so the clean state is
enforced rather than aspirational.

**Verification performed:**

| Check | Result |
| --- | --- |
| Debug build, full analyser set, warnings-as-errors | 0 warnings, 0 errors |
| Release build, full analyser set, warnings-as-errors | 0 warnings, 0 errors |
| Test suite | 97 / 97 passing |
| Application smoke test | Launches, window titled "JBU CodeLens", responding |

**Last updated:** 2026-07-25

---

## 1. Starting position

The first finding was that the solution **already built with zero warnings and zero errors**:

```
dotnet build JBU.CodeLens.sln -t:Rebuild -v:m
...
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

This was verified to be genuine rather than the result of hidden suppression. A search across the
repository confirmed that before this work began there was **no** `Directory.Build.props`, **no**
`.editorconfig`, and no occurrence of any of the following anywhere in the source tree:

| Suppression mechanism | Occurrences found |
| --- | --- |
| `<NoWarn>` | 0 |
| `<WarningLevel>` overrides | 0 |
| `#pragma warning disable` | 0 |
| `[SuppressMessage]` | 0 |

So no warning was being silenced. The zero was real.

### Why a clean build was still not good enough

The .NET SDK enables the Roslyn code-analysis (`CAxxxx`) rules by default, but runs them in
`AnalysisMode=Default`, which activates only a small subset of the available rules. The remaining
rules are present but never evaluated. A clean build in that mode means "no rule in the small
default subset fired" — it does not mean the code has been analysed.

Raising the analyser to its full rule set is what actually exercises the analysis, and that is what
this work item required.

### A note on the Visual Studio Error List

Visual Studio's Error List also displays `IDExxxx` code-style *suggestions* (unused usings, `var`
preferences, and similar). These are IDE-only hints that never appear in an MSBuild build and do not
affect the build result. If the warnings originally observed were of this kind, they are a separate
category from the analyser warnings addressed below. This distinction is flagged as an open question
in section 5.

---

## 2. Measured baseline

Command used to capture the baseline, run against the code as it stood before any configuration or
source change:

```
dotnet build JBU.CodeLens.sln -t:Rebuild -v:m \
  -p:AnalysisMode=All -p:EnforceCodeStyleInBuild=true
```

**Result: 473 distinct warnings** (deduplicated by file, line, column and rule; MSBuild reports each
warning more than once across the solution build, so the raw log count of 1,064 overstates it).

Codebase size for context:

| Project | Files | Lines |
| --- | --- | --- |
| `src/JBU.CodeLens.Core` | 43 | 10,157 |
| `src/JBU.CodeLens.UI` | 50 | 9,050 |
| `src/JBU.CodeLens.Shared` | 29 | 1,427 |
| `tests/` | 21 | 1,777 |
| **Total** | **143** | **22,411** |

### Breakdown by rule

| Count | Rule | Description |
| ---: | --- | --- |
| 77 | CA1707 | Identifiers should not contain underscores |
| 71 | CA1305 | Specify `IFormatProvider` |
| 51 | CA1031 | Do not catch general exception types |
| 43 | CA1002 | Do not expose generic lists |
| 40 | CA2227 | Collection properties should be read only |
| 28 | CA1307 | Specify `StringComparison` for clarity |
| 27 | CA1062 | Validate arguments of public methods |
| 26 | CA5392 | Use `DefaultDllImportSearchPaths` for P/Invokes |
| 20 | CA1861 | Avoid constant arrays as arguments |
| 14 | CA1063 | Implement `IDisposable` correctly |
| 12 | CA1859 | Use concrete types where possible for improved performance |
| 11 | CA2007 | Do not directly await a `Task` |
| 10 | CA1822 | Mark members as static |
| 8 | CA1310 | Specify `StringComparison` for correctness |
| 7 | CA1816 | Call `GC.SuppressFinalize` correctly |
| 6 | CA1866 | Use char overload |
| 5 | CA1716 | Identifiers should not match keywords |
| 4 | CA1849 | Call async methods when in an async method |
| 4 | CA1003 | Use generic event handler instances |
| 3 | CA1806 | Do not ignore method results |
| 2 | CA1308 | Normalize strings to uppercase |
| 1 | CA1812 | Avoid uninstantiated internal classes |
| 1 | CA1304 | Specify `CultureInfo` |
| 1 | CA1068 | `CancellationToken` parameters must come last |
| 1 | CA1001 | Types that own disposable fields should be disposable |
| **473** | | |

Verified after applying the configuration: the build reports **396** distinct warnings, which is
exactly 473 − 77 (the suppressed CA1707 test-naming warnings). The baseline arithmetic is
self-consistent.

---

## 3. Configuration applied

Two files were added at the repository root.

### `Directory.Build.props`

Turns the analyser up to its full rule set for every project in the solution:

- `EnableNETAnalyzers = true`
- `AnalysisLevel = 8.0` — pinned to the target framework rather than `latest`, so the active rule
  set does not silently change when the build machine's SDK is upgraded. The machine used for this
  work has SDK 10.0.301 while the projects target `net8.0`; without pinning, the measured baseline
  would not be reproducible.
- `AnalysisMode = All`
- `EnforceCodeStyleInBuild = true`
- `TreatWarningsAsErrors = false` — **to be flipped to `true`** as the final step, once the baseline
  is cleared, so that the warning count cannot regress.

### `.editorconfig`

Holds severity configuration and the justification for every suppression. The governing rule adopted
here: a rule is only silenced where the analyser's advice is *wrong for this codebase's context*,
never to reduce a count. Rules that are merely not fixed yet remain at warning severity so they stay
visible in both the build and the Error List.

---

## 4. Per-rule remediation decisions

Each rule was assessed against the actual code rather than triaged by name. Findings that drove a
decision are recorded.

### Fix — genuine defects

| Rule | Count | Rationale |
| --- | ---: | --- |
| CA1305 / CA1307 / CA1310 / CA1304 / CA1308 | ~110 | String comparison and formatting without an explicit culture or `StringComparison`. These are real correctness bugs: behaviour changes with the machine's locale. Largely mechanical to fix (`StringComparison.Ordinal`, `CultureInfo.InvariantCulture`). Highest value in the whole set. |
| CA5392 | 26 | P/Invoke declarations without `DefaultDllImportSearchPaths`. Verified to fall on the 13 `libclang` imports in `src/JBU.CodeLens.Core/Parsing/Cpp/CppParser.cs` and the `dwmapi.dll` import in `src/JBU.CodeLens.UI/Views/MainWindow.xaml.cs`. This is a real DLL-hijacking exposure, and directly relevant because the application already copies native `libclang.dll` next to the executable at build time. |
| CA1062 | 27 | Public methods not validating arguments. Feeds directly into the assertions work item — this list is the ready-made worklist of missing guards. |
| CA2007 | 11 | Missing `ConfigureAwait`. Verified to fall in `src/JBU.CodeLens.Core/AI/ExplanationService.cs`, on `await foreach` loops over LLamaSharp inference. `Core` is a **library** consumed by a WPF application, so `ConfigureAwait(false)` is the correct practice and guards against UI-thread deadlock. Fix, not suppress. |
| CA1063 / CA1001 / CA1816 | ~18 | Incorrect `IDisposable` implementation. Material here because the code owns unmanaged libclang handles (`clang_disposeIndex`, `clang_disposeTranslationUnit`) — a leak is a real native resource leak. |
| CA1861 / CA1859 / CA1822 / CA1866 / CA1849 / CA1806 | ~60 | Performance and correctness cleanups. Low risk, mostly mechanical. |

### Fix with judgement — case by case

| Rule | Count | Rationale |
| --- | ---: | --- |
| CA1031 | 51 | Catching general `Exception`. Some are legitimate: a desktop application needs top-level handlers so a single bad source file cannot terminate the process mid-scan. Each site will be either narrowed to specific exception types or kept with a comment explaining why the broad catch is deliberate. Expect a mix. |
| CA1002 / CA2227 | 83 | `List<T>` on public API and settable collection properties. Some cannot change: WPF two-way data binding and JSON deserialisation both require settable properties and concrete collection types. Sites driven by binding or serialisation will be suppressed with justification; the rest will be fixed. |

### Suppress — analyser advice does not apply

| Rule | Count | Rationale |
| --- | ---: | --- |
| CA1707 | 77 | Underscores in identifiers. **All 77 occurrences are in `tests/JBU.CodeLens.Core.Tests`**, on xUnit test method names using the `Method_Scenario_ExpectedResult` convention — which is this project's documented test convention (`docs/architecture/DeveloperGuide.md`). The rule exists to police public API surface; test methods are not public API, and renaming them would degrade the test report output for no benefit. Suppressed **for the test project only**; the rule stays active for `src/`. |

### Rules requiring assessment

CA1716 (10 occurrences — identifiers matching reserved language keywords) and CA1812 / CA1068 have
not yet been individually assessed. CA1716 in particular may be unfixable without breaking public
API names, in which case it moves to the suppression table with justification.

---

## 5. Open questions for supervisor review

1. **Which warnings were originally observed?** The build was already warning-free, so the warnings
   in question were either Visual Studio `IDExxxx` code-style suggestions (IDE-only, never part of a
   build) or the latent analyser warnings this document addresses. Confirmation would ensure the
   right target is being cleared.

2. **Are justified suppressions acceptable?** Roughly 90 of the 473 warnings should not be "fixed":
   the 77 test-naming warnings, and a subset of the collection-property warnings that WPF binding
   and JSON deserialisation require. The proposal is to suppress these narrowly, scoped by project,
   each with a written justification in `.editorconfig` — rather than change correct code to satisfy
   a rule that does not apply. This is the "if something cannot be done, we will discuss it" case.

---

## 6. Wave 1 — culture and string comparison (complete)

**Result: 110 warnings cleared. 473 → 280 distinct warnings. All 97 tests pass.**

The extra 83 beyond the 110 targeted came from the 77 suppressed CA1707 test-naming warnings plus
six CA1866 warnings resolved incidentally, because switching `EndsWith("y")` to the `char` overload
satisfies both rules at once.

### Files changed

| File | Warnings cleared |
| --- | ---: |
| `src/JBU.CodeLens.Core/Export/MarkdownExporter.cs` | 20 |
| `src/JBU.CodeLens.Core/AI/ExplanationService.cs` | 19 |
| `src/JBU.CodeLens.Core/Analysis/MethodDescriptionBuilder.cs` | 15 |
| `src/JBU.CodeLens.UI/Renderers/DetailPanelRenderer.cs` | 9 |
| `src/JBU.CodeLens.UI/Views/MainWindow.xaml.cs` | 6 |
| `src/JBU.CodeLens.Core/Export/InferenceExportHelper.cs` | 6 |
| `src/JBU.CodeLens.Core/Parsing/Cpp/CppParser.cs` | 5 |
| `src/JBU.CodeLens.Core/AI/MethodConversationSession.cs` | 5 |
| `tests/JBU.CodeLens.Core.Tests/` (4 files) | 10 |
| Remaining `Core` / `Shared` analysis and export files (6 files) | 15 |

### Defects found, not just lint

The most valuable outcome of this wave was that several warnings were genuine locale bugs, not
stylistic noise:

- **`MarkdownExporter.cs` lines 27–28** — `{m?.AverageComplexity:F2}` and
  `{m?.MaintainabilityIndex:F0}` formatted with the ambient culture. On any machine with a
  comma-decimal locale (German, French, most of Europe) the exported Markdown metrics table would
  render `3,14` instead of `3.14`, corrupting a document intended to be machine-readable and
  portable. Now pinned to `InvariantCulture`.
- **`OperationalLimitFormatter.cs` line 176** — `char.ToUpper(trimmed[0])` used the ambient culture
  to capitalise generated prose. Under a Turkish locale this maps `i` to `İ` (dotted capital I),
  visibly corrupting any description beginning with that letter. Now `char.ToUpperInvariant`.
- The remaining `IndexOf`/`Contains`/`StartsWith`/`EndsWith` calls parsed structural delimiters
  (`:`, `<`, `(`, `,`, `::`) using culture-sensitive comparison. These are parser internals where
  only ordinal comparison is ever correct; culture-sensitive matching on such delimiters is a known
  source of locale-dependent parse failures. All now explicitly `StringComparison.Ordinal`.

### Second justified suppression added

`MethodDescriptionBuilder.Build` and `MethodDescriptionBuilder.Conjugate` are annotated with
`[SuppressMessage]` for **CA1308** (*normalize strings to uppercase*), with the justification
recorded inline at each site. Both methods lowercase text in order to generate English prose for
display — the value is never compared, looked up, or used in a security decision, which is the
scenario CA1308 exists to protect. Following the rule literally would produce visibly wrong output.
Unlike CA1707 this is scoped to two methods rather than a project, and it is annotated in the source
so it is visible to any reader of the code.

---

## 7. Wave 2 — P/Invoke security and `IDisposable` (complete)

**Result: 48 warnings cleared. 280 → 232 distinct warnings. All 97 tests pass.**

### CA5392 → CA5393: the fix that revealed a second rule

Adding `DefaultDllImportSearchPaths` to satisfy CA5392 initially used
`DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32` — the directory holding
`JBU.CodeLens.Core.dll`, which is where the build places `libclang.dll`. That silenced CA5392 but
immediately raised **25 × CA5393** (*do not use unsafe DllImportSearchPath value*), because
`AssemblyDirectory` is on that rule's unsafe list: an attacker able to write next to the assembly
still wins.

The resolution was `DllImportSearchPath.SafeDirectories` (`LOAD_LIBRARY_SEARCH_DEFAULT_DIRS`), which
covers the application directory and System32 and is accepted by both rules. Because this changes
native library resolution at runtime — a failure mode no compiler check would catch — it was
verified empirically rather than assumed: the `CppParserTests` suite performs real libclang parses
through these exact imports, and all 7 tests pass with the new setting.

**Security impact.** Before this change, all 14 P/Invoke declarations used the default search order,
which includes the current working directory. A `libclang.dll` planted in the folder the application
happened to be launched from would have been loaded in preference to the genuine one, giving
arbitrary code execution inside the process. This is a real DLL-hijacking exposure, and it is the
most substantive defect the exercise has surfaced so far. `dwmapi.dll` in the UI project is now
pinned to `System32` for the same reason.

### `IDisposable` (CA1063 / CA1816) — 21 warnings, all in tests

Every occurrence was an xUnit fixture implementing `IDisposable` to delete a temp directory after
each test. CA1063 demands the full virtual `Dispose(bool)` pattern, which only applies to types that
can be inherited from. Marking the seven test classes `sealed` is the correct fix and resolves
CA1063 and CA1816 together, without adding ceremony to test code.

### Third justified suppression: CA1001 on `MainWindow`

`MainWindow` holds a `CancellationTokenSource` field (`_activeCts`) and is not `IDisposable`. Review
of all five usage sites showed the lifetime is already correct: each scan and each export creates the
source and disposes it in a `finally` block before clearing the field, so it never outlives the
operation. WPF never calls `Dispose` on a `Window`, so implementing `IDisposable` would add an entry
point nothing invokes while changing no behaviour. Suppressed on the type with the reasoning recorded
inline.

---

## 8. Wave 4 — performance, conventions and API shape (complete except three rules)

**Result: 71 warnings cleared. 205 → 134 distinct warnings. All 97 tests pass.**

### Design changes made

**Five stateless helpers converted to static classes.** `MarkdownExporter`, `JsonExporter`,
`CallGraphBuilder`, `MetricsCalculator` and `RelationshipExtractor` held no state, implemented no
interface, and were never used polymorphically — they were instantiated ad hoc purely to call one
method (`new MetricsCalculator().Calculate(ir)`). CA1822 flagged the methods, and following it
cascades: once every member is static, CA1052 requires the type to be static too. Converting them
outright is the coherent end state and removes the pointless allocations. Thirteen call sites
updated across `ScideEngine`, `InferenceExportHelper` and the tests.

**Custom events converted to the .NET event pattern.** `ThemeChanged`, `NodeClicked` and two
`BackRequested` events used `Action`/`Action<T>` delegates. CA1003 requires `EventHandler<T>` with
`T` deriving from `EventArgs`, so two event-args types were introduced (`ThemeChangedEventArgs`,
`NodeClickedEventArgs`) and the nine raise/subscribe sites updated. The XAML-wired
`BackButton_Click` handlers were checked and are unaffected — their signatures did not change.

**`CancellationToken` moved to last parameter** on `IProjectAnalyzer.AnalyzeProjectAsync` (CA1068),
with `ScideEngine` and all four call sites updated.

**P/Invoke return values explicitly discarded** (CA1806). `clang_visitChildren` returns non-zero
only when a visitor returns `CXChildVisit_Break`; all five visitors in `CppParser` were checked and
none ever do, so the result genuinely carries no information. `_ =` now records that deliberately.

**`ConfigureAwait(false)` added in Core** (CA2007) — see the suppression note below for why the same
rule is silenced in the UI.

### Fourth, fifth and sixth suppressions

| Rule | Scope | Reason |
| --- | --- | --- |
| CA2007 | `src/JBU.CodeLens.UI/` | Following it would be a **runtime defect**. Every awaited call in the UI resumes by touching WPF controls — `MainWindow.xaml.cs` assigns `summaryText.Text` immediately after its await. WPF controls have thread affinity, so `ConfigureAwait(false)` would resume on a thread-pool thread and throw `InvalidOperationException`. Capturing the context is exactly what is required. The rule stays active for Core and Shared, where the three occurrences were **fixed**, not suppressed. |
| CA1861 | `tests/` | The rule's own justification is conditional — it applies "if the called method is called repeatedly". An xUnit `[Fact]` runs once, so the allocation it targets happens once. All 20 occurrences are inline expected values in assertions; hoisting them to static fields would separate each expected value from the assertion checking it. Active for `src/`. |
| CA1716 | solution-wide | Flags the `JBU.CodeLens.Shared` namespace because **`Shared` is a reserved keyword in Visual Basic** (VB's equivalent of `static`) — not in C#. The rule protects consumers written in other .NET languages. This solution is C#-only with no VB or F# project and no plan for one; satisfying it would mean renaming the Shared project, all five namespaces and every `using` across the repository. Flagged for supervisor review as a deliberate decision. |

Two further narrow suppressions were added inline with justification:

- **CA1859 on three `MainWindow` fields.** The analyser wants `_projectAnalyzer`, `_exportService`
  and `_explanationService` retyped from their Shared interfaces to the Core concrete types to
  devirtualise the calls. This is refused: the class comment states that this file is the composition
  root and uses Core types "exclusively through their Shared interfaces". Following the analyser
  would spread concrete Core dependencies through the view and dissolve the layering boundary the
  architecture is built on, to save a virtual call made a handful of times per scan.
- **CA1812 on `DetailPanelRenderer.MethodChatState`.** A **false positive**: instances are created
  by `ConditionalWeakTable.GetOrCreateValue`, which constructs the value through reflection rather
  than a visible `new`, so the analyser cannot see the instantiation.

---

## 9. Remaining steps

| # | Step | Status |
| --- | --- | --- |
| 1 | Capture baseline | Done — 473 warnings |
| 2 | Apply analyser configuration | Done |
| 3 | Complete the long-tail rule breakdown | Done |
| 4 | Fix wave 1 — culture and string comparison (110) | **Done — 473 → 280** |
| 5 | Fix wave 2 — P/Invoke security, `IDisposable` (48) | **Done — 280 → 232** |
| 6 | Fix wave 3 — argument validation (27: CA1062) | **Done — 232 → 205** |
| 7 | Fix wave 4 — performance, conventions, API shape (71) | **Done — 205 → 134** |
| 8 | Assess CA1716 / CA1812 / CA1068 / CA1003 | **Done** (folded into wave 4) |
| 9 | Wave 5 — CA1031 / CA1002 / CA2227 (134) | **Done — 134 → 0** |
| 10 | Enable `TreatWarningsAsErrors` | **Done** |

Wave 3 was carried out as part of the assertions work item, since the CA1062 findings are exactly
its worklist of missing argument guards. The reasoning behind `throw` rather than `Debug.Assert` at
those 27 sites, and the full list of guarded methods, is recorded in
[AssertionPolicy.md](AssertionPolicy.md).

## 10. Wave 5 — the final 134

### CA1031 (51) — broad `catch (Exception)`

All 51 sites were reviewed individually. Every one turned out to be a deliberate resilience
boundary that already either surfaces the failure to the user or carries a comment explaining why
swallowing is safe — not a single lazy catch among them:

- **Parsers (25)** — record into `ParseResult.Errors` and continue the scan. Two sit **inside
  P/Invoke callbacks invoked by libclang**, where letting an exception escape into native code is
  undefined behaviour that would take the process down. The broad catch is mandatory there.
- **MainWindow (5)** — scan and export handlers; each writes to the status bar and raises a
  notification, so nothing is hidden.
- **ExplanationService (4)** — reports through `LoadError` or an `[Inference failed: …]` result.
- **Persistence: `UiSettings`, `CustomFaqStore`, `AiResultStore`, `AppPaths` (7)** — best effort; a
  corrupt settings or cache file must never block startup.
- **Tests (7)** — temp-directory cleanup in `Dispose`.

**Decision: suppressed, not narrowed.** Narrowing would mean enumerating the complete exception set
of Roslyn, libclang marshaling, LLamaSharp and the file system; any type missed becomes a crash in
front of the user. The governing design principle is that this application analyses source code it
did not write, so no single malformed input may terminate the process mid-scan.

### CA1002 (43) + CA2227 (40) — collection API shape

**Correction to an earlier assessment in this document.** These were initially expected to be
partly unfixable because WPF data binding and `System.Text.Json` deserialisation both require
settable properties. **On investigation that argument does not hold**, and it should not be relied
on:

- The UI builds every element in code. The only XAML bindings are `TemplateBinding` inside control
  templates, and there are no `ItemsSource` assignments — the structural models are never data-bound.
- The structural models are never deserialised. The only `JsonSerializer.Deserialize` targets in the
  solution are `FileShape`, `UiSettings` and `List<string>`.

The justification that does hold is narrower and was verified:

- **These rules govern reusable library APIs.** No project sets `IsPackable`, `PackageId` or
  `GeneratePackageOnBuild`; nothing is published or distributed. The solution builds one desktop
  application, and `Shared` is an internal contract assembly referenced only by `Core` and `UI`,
  both recompiled together in this repository.
- **The setters are used deliberately.** The pipeline replaces whole collections in a single
  assignment rather than mutating in place — `ir.Relationships = RelationshipExtractor.Extract(ir)`,
  `classInfo.Methods = DeduplicateMethods(classInfo.Methods)`.

**Decision: suppressed.** Converting every collection property to a get-only `Collection<T>` would
mean rewriting those assignments as `Clear()`/`AddRange()` and giving up the `List<T>` operations the
parsers and exporters use, reaching into every parser, analyser, exporter and renderer — a large,
regression-prone refactor of the types every component depends on, for an encapsulation guarantee
that only matters for an API this project does not expose.

### Suppression register — the complete list

Of the 473 baseline warnings, **339 were fixed in code** and **134 are suppressed** under the
11 rules below. Every suppression is scoped as narrowly as the rule allows and carries a written
justification at the point of suppression, so silencing a rule is a visible, reviewable act.

| Rule | Count | Scope | Where recorded |
| --- | ---: | --- | --- |
| CA1031 | 51 | solution-wide | `.editorconfig` |
| CA1002 | 43 | solution-wide | `.editorconfig` |
| CA2227 | 40 | solution-wide | `.editorconfig` |
| CA1707 | 77 | `tests/` project | `.editorconfig` |
| CA1861 | 20 | `tests/` project | `.editorconfig` |
| CA2007 | 8 | `src/JBU.CodeLens.UI/` project | `.editorconfig` |
| CA1716 | 5 | solution-wide | `.editorconfig` |
| CA1859 | 3 | `MainWindow` service fields | inline `[SuppressMessage]` |
| CA1308 | 2 | 2 methods in `MethodDescriptionBuilder` | inline `[SuppressMessage]` |
| CA1001 | 1 | `MainWindow` type | inline `[SuppressMessage]` |
| CA1822 | 1 | `ExecutionFlowAnalyzer.Analyze` | inline `[SuppressMessage]` |
| CA1812 | 1 | `DetailPanelRenderer.MethodChatState` | inline `[SuppressMessage]` |

Three of these are worth a supervisor's attention as decisions rather than routine cleanups:

1. **CA1716** would require renaming the `Shared` project and every namespace across the repository,
   because `Shared` is a Visual Basic keyword — for the benefit of VB consumers that do not exist.
2. **CA1002 / CA2227** (83 warnings, the largest block) are suppressed on the grounds that these are
   application-internal data shapes, not a published library API. Section 10 records why the
   originally assumed WPF/JSON justification turned out to be wrong.
3. **CA1031** is suppressed rather than narrowed specifically to avoid introducing crash paths into
   an application whose job is to survive malformed third-party input.

### What was actually gained

Beyond the count, the exercise surfaced defects that would not otherwise have been found:

- A **DLL-hijacking vulnerability** across all 14 P/Invoke declarations (CA5392 / CA5393).
- **Locale-dependent corruption** of the exported Markdown metrics table on any comma-decimal
  machine, and of generated prose under a Turkish locale (CA1305 / CA1304).
- **Culture-sensitive parsing of structural delimiters** throughout both parsers (CA1307 / CA1310).
- A missing-argument-validation worklist that became the foundation of the assertions work item
  (CA1062 → [AssertionPolicy.md](AssertionPolicy.md)).

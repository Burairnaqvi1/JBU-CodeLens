# Assertion and Argument-Validation Policy

**Work item:** Add assertions for critical checks using `System.Diagnostics`, so that a failing
condition is caught during development.
**Status:** Policy defined; public-API guards applied across all 27 identified entry points;
first invariant assertions added.
**Last updated:** 2026-07-25

---

## 1. Starting position

Before this work the entire codebase — 22,411 lines across 143 files — contained **three**
assertion or guard statements in total. Neither of the two mechanisms described below was applied
systematically anywhere.

---

## 2. The policy

The requirement is often stated as "add assertions", but two different mechanisms are needed and
using the wrong one is a defect rather than a style choice. The deciding question is **who can cause
the condition to fail**.

### `Debug.Assert` — internal invariants

```csharp
Debug.Assert(depth >= 1, $"Inheritance depth must be at least 1, got {depth}.");
```

Use when the condition **cannot** fail unless the code itself is wrong: a postcondition of an
internal algorithm, a relationship between two values this class computed, a state that an earlier
step in the same component guaranteed.

- Compiled **only when the `DEBUG` symbol is defined**. It disappears entirely from a Release
  build — no runtime cost, and no protection either.
- This is exactly the behaviour asked for: it compiles in Debug mode and trips when the condition
  fails.

### `throw` — validation of external input

```csharp
ArgumentNullException.ThrowIfNull(ir);
```

Use when the condition **can** legitimately fail at runtime in a shipped build: arguments arriving
at a public method, file paths, parsed source text, model files, anything a caller or a user
supplies.

- Compiled into **every** configuration, Release included.
- Produces a specific, catchable exception type rather than terminating.

### The mistake this policy exists to prevent

Using `Debug.Assert` for the second category is a classic error: the check passes review, works
throughout development, and then **silently vanishes from the shipped build**, leaving the exact
production input that needed validating completely unguarded. The two mechanisms are not
interchangeable and the choice is not stylistic.

### Decision rule

| The condition can be broken by… | Mechanism | Survives Release? |
| --- | --- | --- |
| A caller passing bad arguments to a public method | `ArgumentNullException.ThrowIfNull` / `ArgumentException` | Yes |
| User input: file paths, source files, model files | `throw` with a specific exception type | Yes |
| This component's own earlier computation | `Debug.Assert` | No |
| A postcondition of a private/internal algorithm | `Debug.Assert` | No |
| A relationship between values this class just derived | `Debug.Assert` | No |

---

## 3. Runtime behaviour of a failed assertion — verified, not assumed

How `Debug.Assert` behaves on failure depends on the host, and the answer determines whether the
mechanism is usable in an automated test run at all. Rather than assume, this was measured: a
temporary probe test calling `Debug.Assert(false, …)` was added alongside a second, passing test,
the suite was run, and the probe was then removed.

**Result:**

```
[xUnit.net] JBU.CodeLens.Core.Tests.TempAssertProbe.ProbeAssertFailure [FAIL]
Failed!  - Failed: 1, Passed: 1, Skipped: 0, Total: 2
```

The failing assertion **failed only its own test**; the second test still ran and passed, and the
test host was not terminated. There was no modal dialog and no lost run.

**Consequence:** no custom `TraceListener` or assertion-handling infrastructure is needed for the
test project. `Debug.Assert` integrates with the existing suite as-is, and any invariant broken by a
future change will surface as an ordinary, attributable test failure.

*(Not verified: behaviour inside the running WPF application under a Debug build. That path is
developer-only — assertions are compiled out of Release — so a debugger break or dialog there is
acceptable and arguably desirable. It is recorded here as untested rather than claimed.)*

---

## 4. Applied: public-API guards (27 entry points)

The `CA1062` analyser findings from the warning-removal work item provided the worklist: every
externally visible method dereferencing a reference-type parameter without validating it. All 27
sites now guard with `ArgumentNullException.ThrowIfNull`, chosen over a hand-written
`if (x is null) throw` because it captures the parameter name automatically and cannot drift out of
sync with a rename.

| Area | Methods guarded |
| --- | --- |
| Structural analysis | `CallGraphBuilder.Build`, `MetricsCalculator.Calculate`, `RelationshipExtractor.Extract`, `SymbolTable.Add`, `SymbolTable.BuildFrom` |
| Knowledge graph | `KnowledgeGraph.AddNode`, `KnowledgeGraph.BuildFrom` |
| Export | `MarkdownExporter.Export`, `JsonExporter.Export`, `InferenceExportHelper.BuildMarkdown`, `InferenceExportHelper.BuildJson` |
| Deterministic analysis | `InferenceEngine.Analyze`, `ExecutionFlowAnalyzer.Analyze`, `CategoryClassifier.Classify`, `ClassDescriptionBuilder.Build`, `MethodDescriptionBuilder.Build`, `MethodAnalysisContext` constructor |
| Engine / lookup | `ScideEngine.GetProjectSummaryFallback`, `ScideEngine.BuildMethodDetailContext`, `ScideMethodIndex.Lookup`, `ScideMethodIndex.LookupType` |
| AI and stores | `ExplanationService.GenerateClassSummary`, `AiResultStore.Save`, `CustomFaqStore.Add` |

These are all **`throw`** rather than assert: they sit on the public surface of `Core` and `Shared`,
so a null arriving there is a caller error that must be reported in Release too.

All 97 tests pass with the guards in place.

---

## 5. Applied: first invariant assertions

Assertions were placed on conditions that are genuinely impossible unless the surrounding algorithm
is wrong — not sprinkled to raise a count. Each carries a message naming the offending value, so a
failure is diagnosable without a debugger.

| Location | Invariant | Why it matters |
| --- | --- | --- |
| `MetricsCalculator.Calculate` | `MaxComplexity >= AverageComplexity` | The two figures are aggregated separately over the same method set. A future change to one aggregation that disagrees with the other would otherwise produce quietly wrong numbers in the exported report rather than an error. |
| `MetricsCalculator.CalculateMaxInheritanceDepth` | `depth >= 1` | Every type sits at depth 1 even with no base type, and a cycle guard bounds the walk. Zero or negative would mean the seed value or the guard had been broken. |
| `MetricsCalculator.CalculateMaintainabilityIndex` | `documentedRatio` in `[0, 1]` | A count of matching classes over the total can only land in that range; outside it, numerator and denominator have drifted apart and the published index is meaningless. |
| `ExecutionFlowAnalyzer.NumberSteps` | list non-empty, step numbers contiguous and 1-based | `CapSteps` guarantees at least one step even for an empty method, and both the UI and the exporters render this as an ordered `1., 2., 3.` sequence. A gap or an empty list would silently emit a malformed execution-flow section. |

Each of these is a **postcondition of a computation the class performed itself**, which is precisely
the `Debug.Assert` category in the decision rule above.

---

## 6. Remaining steps

| # | Step | Status |
| --- | --- | --- |
| 1 | Define the policy | Done |
| 2 | Verify assertion behaviour under the test host | Done — fails only the owning test |
| 3 | Apply public-API guards (27 sites) | Done |
| 4 | First invariant assertions in metrics and execution-flow analysis | Done |
| 5 | Extend invariant coverage to the parsers (`CSharpParser`, `CppParser`) | Not started |
| 6 | Extend invariant coverage to `ScideEngine` pipeline stages | Not started |
| 7 | Review AI-path invariants in `ExplanationService` | Not started |

### Note on the parser work in step 5

`CppParser` marshals native libclang handles. The distinction matters there and needs care: a null
handle returned by `clang_createIndex` or `clang_parseTranslationUnit` is a **runtime failure**
(missing or corrupt native library, unreadable source file), not a broken invariant — so those cases
belong in the `throw` category, not `Debug.Assert`. Assertions in that file should be reserved for
post-marshaling structural expectations.

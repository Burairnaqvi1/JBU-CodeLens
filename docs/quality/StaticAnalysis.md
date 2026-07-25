# Static Analysis

**Work item:** Use static analysis tools (Understand, MathWorks Polyspace).
**Status:** Second analysis engine installed and running in the build; security-class findings
fixed; maintainability findings enumerated as a tracked backlog.
**Last updated:** 2026-07-25

---

## 1. Polyspace cannot analyse this project

**MathWorks Polyspace (Bug Finder and Code Prover) supports C, C++ and Ada only. It does not
support C#.**

This is a language-support limitation, not a licensing or configuration problem. The repository was
checked directly: it contains **no `.c`, `.cpp`, `.h` or `.hpp` files at all**. The solution is
100 % C# — three C# libraries and a WPF application, 22,411 lines across 143 files. There is
literally nothing for Polyspace to point at.

The project *parses* C++ (through libclang, in `CppParser`), which is presumably where the
expectation came from, but the product itself contains no C++ source.

### Options considered

| Option | Verdict |
| --- | --- |
| Run Polyspace on the product | **Impossible** — no C/C++ source exists |
| Add C++ sample files as parser fixtures and run Polyspace on those | Possible, but dishonest as a quality gate: it would analyse *test input data*, not the product. Available as a demonstration if specifically required. |
| Substitute an equivalent tool that supports C# | **Adopted** — see below |

## 2. Understand (SciTools)

Understand **does** support C#, so the other named tool is viable and remains applicable to this
codebase. Its dependency graphs and complexity metrics also feed directly into the design-document
work item. It is a commercial, separately licensed desktop tool, so it is a manual analysis step
rather than something wired into the build.

## 3. Substitute for Polyspace: SonarAnalyzer.CSharp

**SonarSource's C# analysers** were adopted as the second engine — the same rule engine SonarQube
runs, packaged as Roslyn analysers.

Why this substitution is a fair one:

- **It is a genuinely independent engine from a different vendor.** The point of the requirement is
  a second opinion, and Sonar's rule set overlaps only partly with Microsoft's CA rules — as the
  results below demonstrate, it found 162 issues the CA rules did not report.
- **It runs inside the normal build.** No server to stand up, no license, no separate scan step that
  someone has to remember to run, and no way for it to silently stop being applied.
- **It is free**, unlike Polyspace and Understand.

Installed in `Directory.Build.props` as a solution-wide analyser with `PrivateAssets="all"`, so it
is build-time only and is never published or referenced transitively:

```xml
<PackageReference Include="SonarAnalyzer.CSharp" Version="10.30.0.144632" PrivateAssets="all" />
```

Note that the Microsoft CA rules from the warning-removal work item are themselves a static
analysis engine (Roslyn), so the project now runs **two independent analysers on every build**.

---

## 4. Results of the first Sonar run

**162 distinct findings**, none of which the Microsoft CA rules had reported.

| Count | Rule | Finding |
| ---: | --- | --- |
| 87 | S6444 | Regular expression with no timeout |
| 20 | S3400 | Method should not return a constant |
| 20 | S3267 | Loop should be simplified with LINQ |
| 9 | S8949 | `CancellationToken` not passed to an overload that accepts one |
| 6 | S125 | Commented-out code |
| 4 | S3358 | Nested ternary |
| 3 | S8969 | Redundant null-forgiving operator |
| 3 | S1066 | Collapsible `if` |
| 2 | S1871 | Identical branches |
| 1 each | S6670, S4144, S3241, S2325, S1172, S1144, S1121, S108 | Assorted single findings |

### The significant finding: 87 unbounded regular expressions (ReDoS)

`S6444` accounted for 54 % of the findings, and it is a real vulnerability rather than a style
complaint.

**The exposure.** All 87 patterns run over **source files the user did not write** — that is the
entire purpose of this application. .NET's regex engine backtracks, so an adversarial or merely
pathological input can drive a match exponentially. With no timeout the match never returns: the
scan hangs indefinitely, with no error, no cancellation and no diagnostic. A single crafted source
file placed in a scanned folder is enough.

They were concentrated in exactly the wrong place — the analysis engine:

| File | Count |
| --- | ---: |
| `Analysis/RuleEngine.cs` | 23 |
| `Analysis/ExecutionFlowAnalyzer.cs` | 14 |
| `AI/ExplanationService.cs` | 14 |
| `Analysis/PostconditionAnalyzer.cs` | 7 |
| `Analysis/DesignConstraintAnalyzer.cs` | 6 |
| `Analysis/AnalysisMessageBuilder.cs` | 6 |
| Six further analysis files | 17 |

**The fix.** `Core/Utilities/SafeRegex.cs` provides drop-in replacements for the static `Regex`
helpers that always apply a two-second match timeout, and all 86 static call sites now use it. The
one remaining case — a compiled `Regex` field in `CppParser` — takes the timeout directly in its
constructor.

Two seconds is orders of magnitude beyond what these patterns need on a normal source file (they
complete in microseconds), so the timeout only ever fires on genuinely pathological input. When it
does, `RegexMatchTimeoutException` is raised, and the parsers' existing per-file error handling —
the same broad catches audited under CA1031 — records it and moves on. **One hostile file now
degrades to one reported parse error instead of freezing the application.**

All 97 tests pass with the timeouts in place.

---

## 5. Phase 2 backlog — the remaining 75 findings

These are maintainability and style findings with no security or correctness impact. They are set to
`suggestion` severity in `.editorconfig` rather than switched off: they remain visible in the Visual
Studio editor and under Error List → Messages, but do not fail the build while the work is
outstanding.

The reasoning for not clearing them immediately: `TreatWarningsAsErrors` is enabled, and adopting a
450-rule engine wholesale on an existing codebase in one step would either block every build on
pre-existing style debt or force the engine to be disabled entirely. Phasing keeps the engine
running and the findings enumerated. Every rule is listed above with its count, so the backlog is
counted, not hidden.

### Two that are unlikely to be adopted

- **S3267 (20) — "loop should be simplified with LINQ."** Raised against per-AST-node loops in the
  parsers and analysers. The current form avoids a delegate invocation and an allocation per
  element on the hottest path in the application. Sonar classes this as a code smell, not a defect.
- **S3400 (20) — "method should not return a constant."** Raised against the `AnalysisMessageBuilder`
  message catalogue. The method form is the deliberate seam through which those messages get
  parameterised; collapsing them to constants would have to be undone the first time a message
  needs a value interpolated.

### Worth doing when the backlog is picked up

- **S8949 (9)** — pass `CancellationToken` to `Task.Run` overloads that accept one, in the export
  and scan paths. Minor, but it makes cancellation intent explicit at each site.
- **S1144, S1172, S8969, S3241** — dead private method, unused parameter, redundant null-forgiving
  operators, unused return value. Straightforward removals.
- **S125 (6)** — commented-out code blocks; each needs a judgement call on whether the comment is
  documentation or residue.

---

## 6. Summary for review

| Requirement | Outcome |
| --- | --- |
| Polyspace | **Not possible** — C/C++/Ada only; this project has no C/C++ source |
| Understand | Supports C#; remains viable as a manual analysis and metrics step |
| Substitute engine | **SonarAnalyzer.CSharp installed and running on every build** |
| Engines now active | **Two** — Microsoft Roslyn CA rules + SonarSource C# rules |
| Findings from the new engine | 162, none previously reported |
| Security-class findings | **87 ReDoS exposures fixed** |
| Remaining | 75 maintainability findings, enumerated as a tracked backlog |
| Build state | 0 warnings, 0 errors, Debug and Release, warnings-as-errors enabled |
| Tests | 97 / 97 passing |

# JBU.CodeLens Documentation

## Supervisor review — the four work items

| # | Work item | Deliverable | Status |
| --- | --- | --- | --- |
| 1 | Remove all build warnings of every kind | [quality/WarningRemediation.md](quality/WarningRemediation.md) | **Complete — 473 → 0**, enforced by `TreatWarningsAsErrors` |
| 2 | Assertions for critical checks (`System.Diagnostics`) | [quality/AssertionPolicy.md](quality/AssertionPolicy.md) | **Complete** — policy defined, 27 public guards + invariant assertions applied |
| 3 | Use static analysis tools | [quality/StaticAnalysis.md](quality/StaticAnalysis.md) | **Complete** — Polyspace shown to be inapplicable; SonarAnalyzer adopted and running |
| 4 | Software design document, flow/state diagrams | [design/SDD.md](design/SDD.md) | **Complete** — IEEE 1016 structure with seven diagrams |

### Points needing a decision or acknowledgement

1. **Polyspace cannot analyse this project.** It supports C, C++ and Ada only; the repository
   contains no C/C++ source. SonarAnalyzer.CSharp was adopted as the substitute — a genuinely
   independent second engine that runs on every build. ([StaticAnalysis.md §1](quality/StaticAnalysis.md))
2. **134 of the 473 warnings are suppressed rather than fixed**, under 11 rules, each with a written
   justification at the point of suppression. The register is in
   [WarningRemediation.md](quality/WarningRemediation.md).
3. **CA1716 flags the `Shared` namespace** because `Shared` is a Visual Basic keyword. Satisfying it
   would mean renaming the project and every namespace repository-wide, for the benefit of VB
   consumers that do not exist. Declined and documented.
4. **75 Sonar findings remain as a tracked backlog** — all maintainability/style, no security or
   correctness impact. Each is listed with its count in [StaticAnalysis.md §5](quality/StaticAnalysis.md).

### Defects found and fixed along the way

These were not visible before the analysers were turned up, and are the substantive return on the
exercise:

- **DLL-hijacking exposure** on all 14 P/Invoke declarations — a planted `libclang.dll` in the
  launch directory would have been loaded in preference to the real one.
- **ReDoS exposure** — 87 unbounded regular expressions running over source files the user did not
  write; one pathological input could hang a scan indefinitely with no error.
- **Locale-dependent corruption** of the exported Markdown metrics table on any comma-decimal
  machine, and of generated prose under a Turkish locale.
- **Culture-sensitive parsing of structural delimiters** throughout both parsers.

### Verification

Every claim above was verified, not assumed:

| Check | Result |
| --- | --- |
| Debug build, two analysers, warnings-as-errors | 0 warnings, 0 errors |
| Release build | 0 warnings, 0 errors |
| Test suite | 97 / 97 passing |
| Application smoke test | Launches, window titled "JBU CodeLens", responding |

---

## Architecture reference

| Document | Covers |
| --- | --- |
| [design/SDD.md](design/SDD.md) | **Start here** — full design description with diagrams |
| [architecture/Architecture.md](architecture/Architecture.md) | Layers, dependency rules, scan pipeline |
| [architecture/DeveloperGuide.md](architecture/DeveloperGuide.md) | Build, run, test, conventions |
| [architecture/ProjectIR.md](architecture/ProjectIR.md) | The project-wide intermediate representation |
| [architecture/Parser.md](architecture/Parser.md) | C# and C++ parser design |
| [architecture/LLM.md](architecture/LLM.md) | Local model integration |
| [architecture/Exporter.md](architecture/Exporter.md) | Word / Markdown / JSON export |
| [architecture/Backend_API.md](architecture/Backend_API.md) | Service contracts |
| [architecture/Roadmap.md](architecture/Roadmap.md) | Planned work |
| [architecture/KnownLimitations.md](architecture/KnownLimitations.md) | Known limitations |

## Quality and performance

| Document | Covers |
| --- | --- |
| [quality/WarningRemediation.md](quality/WarningRemediation.md) | Warning baseline, per-rule decisions, suppression register |
| [quality/AssertionPolicy.md](quality/AssertionPolicy.md) | `Debug.Assert` vs `throw` policy and where each is applied |
| [quality/StaticAnalysis.md](quality/StaticAnalysis.md) | Analysis engines, findings, triage |
| [perf/baseline_report.md](perf/baseline_report.md) | Performance baseline |
| [perf/improvement_report.md](perf/improvement_report.md) | Performance improvements |

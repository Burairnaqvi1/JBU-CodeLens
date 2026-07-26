using JBU.CodeLens.Core.Analysis;
using JBU.CodeLens.Shared.Models;

namespace JBU.CodeLens.Core.Tests;

/// <summary>
/// Covers the deterministic per-method analysers: the rules that infer preconditions,
/// postconditions and runtime risks from a method's source text.
/// </summary>
/// <remarks>
/// These analysers hold the densest logic in the codebase and had no direct tests, which made any
/// refactor of them unverifiable. Assertions target the rule identifier and the subject rather than
/// the generated wording, so a change to how a message reads does not break a test, but a change
/// to which rule fires — or to which parameter it blames — does.
/// </remarks>
public class AnalyzerRuleTests
{
    private static MethodAnalysisContext Context(
        string sourceBody,
        string[]? parameters = null,
        string returnType = "void",
        string fileName = "Sample.cs")
    {
        var parentClass = new ClassInfo
        {
            Name = "Sample",
            SourceFilePath = @"C:\proj\" + fileName,
        };

        var method = new MethodInfo
        {
            Name = "Operate",
            ReturnType = returnType,
            AccessModifier = "public",
            ParentClass = parentClass,
        };

        foreach (var p in parameters ?? [])
        {
            method.Parameters.Add(p);
        }

        method.XmlDocTags["sourceCode"] = sourceBody;
        parentClass.Methods.Add(method);
        return new MethodAnalysisContext(method);
    }

    // ── Preconditions ────────────────────────────────────────────────────────

    [Fact]
    public void NullGuardFollowedByThrow_ProducesPreconditionNamingTheParameter()
    {
        var context = Context(
            """
            public void Operate(Order order)
            {
                if (order == null) throw new ArgumentNullException(nameof(order));
                order.Process();
            }
            """,
            parameters: ["Order order"]);

        var results = new PreconditionAnalyzer().Analyze(context);

        Assert.Contains(results, r => r.RuleId == "guard-null" && r.Subject == "order");
    }

    [Fact]
    public void NullCheckWithoutThrow_ProducesNoGuardPrecondition()
    {
        // The rule requires a throw after the guard; a check that merely returns is not a
        // preconditionion the caller must satisfy.
        var context = Context(
            """
            public void Operate(Order order)
            {
                if (order == null) return;
                order.Process();
            }
            """,
            parameters: ["Order order"]);

        var results = new PreconditionAnalyzer().Analyze(context);

        Assert.DoesNotContain(results, r => r.RuleId == "guard-null");
    }

    [Fact]
    public void ThrowIfNull_ProducesPrecondition()
    {
        var context = Context(
            """
            public void Operate(Order order)
            {
                ArgumentNullException.ThrowIfNull(order);
            }
            """,
            parameters: ["Order order"]);

        var results = new PreconditionAnalyzer().Analyze(context);

        Assert.Contains(results, r => r.RuleId == "throw-if-null" && r.Subject == "order");
    }

    [Fact]
    public void ZeroGuardFollowedByThrow_ProducesPrecondition()
    {
        var context = Context(
            """
            public int Operate(int divisor)
            {
                if (divisor == 0) throw new ArgumentException("divisor");
                return 100 / divisor;
            }
            """,
            parameters: ["int divisor"],
            returnType: "int");

        var results = new PreconditionAnalyzer().Analyze(context);

        Assert.Contains(results, r => r.RuleId == "guard-eq-zero" && r.Subject == "divisor");
    }

    [Fact]
    public void RangeGuard_ProducesRangeCheckPrecondition()
    {
        var context = Context(
            """
            public void Operate(int index)
            {
                if (index < 0 || index > 100) throw new ArgumentOutOfRangeException(nameof(index));
            }
            """,
            parameters: ["int index"]);

        var results = new PreconditionAnalyzer().Analyze(context);

        Assert.Contains(results, r => r.RuleId == "guard-range-check" && r.Subject == "index");
    }

    [Fact]
    public void RangeGuard_ReportsEachParameterOnlyOnce()
    {
        // Several range patterns match the same parameter here (the "<", ">" and ">=" patterns all
        // fire on 'index'), yet the user must be shown one precondition, not three.
        //
        // Two layers currently guarantee that: the rule keeps a set of names it has already
        // reported, and Analyze runs a final deduplication keyed on subject and description. They
        // are redundant — breaking either one alone leaves this test passing, and only breaking
        // both makes it fail. The test deliberately pins the user-visible outcome rather than
        // either mechanism, so it stays valid if one of them is ever removed.
        var context = Context(
            """
            public void Operate(int index)
            {
                if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));
                if (index > 100) throw new ArgumentOutOfRangeException(nameof(index));
                if (index >= 100) throw new ArgumentOutOfRangeException(nameof(index));
            }
            """,
            parameters: ["int index"]);

        var results = new PreconditionAnalyzer().Analyze(context);

        Assert.Equal(1, results.Count(r => r.RuleId == "guard-range-check"));
    }

    [Fact]
    public void EmptySourceBody_ProducesNoGuardDerivedPreconditions()
    {
        var context = Context(string.Empty, parameters: ["Order order"]);

        var results = new PreconditionAnalyzer().Analyze(context);

        Assert.DoesNotContain(results, r => r.RuleId is "guard-null" or "guard-eq-zero" or "guard-range-check");
    }

    [Fact]
    public void CppNullptrGuard_IsRecognised()
    {
        var context = Context(
            """
            void Operate(Order* order)
            {
                if (order == nullptr) throw std::invalid_argument("order");
            }
            """,
            parameters: ["Order* order"],
            fileName: "sample.cpp");

        var results = new PreconditionAnalyzer().Analyze(context);

        Assert.Contains(results, r => r.RuleId == "guard-cpp-null" && r.Subject == "order");
    }

    // ── Runtime risks ────────────────────────────────────────────────────────

    [Fact]
    public void DivisionByUnguardedParameter_ReportsDivideByZeroRisk()
    {
        var context = Context(
            """
            public int Operate(int divisor)
            {
                return 100 / divisor;
            }
            """,
            parameters: ["int divisor"],
            returnType: "int");

        var results = new RuntimeRiskAnalyzer().Analyze(context);

        Assert.Contains(results, r => r.RuleId == "divide-by-zero");
    }

    [Fact]
    public void DivisionWithVisibleGuard_ReportsNoDivideByZeroRisk()
    {
        var context = Context(
            """
            public int Operate(int divisor)
            {
                if (divisor != 0)
                {
                    return 100 / divisor;
                }

                return 0;
            }
            """,
            parameters: ["int divisor"],
            returnType: "int");

        var results = new RuntimeRiskAnalyzer().Analyze(context);

        Assert.DoesNotContain(results, r => r.RuleId == "divide-by-zero");
    }

    [Fact]
    public void IntParse_ReportsBothFormatAndOverflowRisks()
    {
        var context = Context(
            """
            public int Operate(string text)
            {
                return int.Parse(text);
            }
            """,
            parameters: ["string text"],
            returnType: "int");

        var results = new RuntimeRiskAnalyzer().Analyze(context);

        Assert.Contains(results, r => r.ExceptionType == "FormatException");
        Assert.Contains(results, r => r.ExceptionType == "OverflowException");
    }

    [Fact]
    public void MemberAccessWithoutNullGuard_ReportsNullDereferenceRisk()
    {
        var context = Context(
            """
            public void Operate(Order order)
            {
                order.Process();
            }
            """,
            parameters: ["Order order"]);

        var results = new RuntimeRiskAnalyzer().Analyze(context);

        Assert.Contains(results, r => r.RuleId == "null-dereference");
    }

    [Fact]
    public void MemberAccessWithNullGuard_ReportsNoNullDereferenceRisk()
    {
        var context = Context(
            """
            public void Operate(Order order)
            {
                if (order == null) throw new ArgumentNullException(nameof(order));
                order.Process();
            }
            """,
            parameters: ["Order order"]);

        var results = new RuntimeRiskAnalyzer().Analyze(context);

        Assert.DoesNotContain(results, r => r.RuleId == "null-dereference");
    }

    // ── Every analyser tolerates a method with no source at all ──────────────

    [Fact]
    public void AllAnalysers_HandleMethodWithNoSourceBody()
    {
        // A C++ method parsed without a recoverable body reaches the analysers with an empty
        // source. None of them may throw: a scan must survive it.
        var context = Context(string.Empty);

        var exception = Record.Exception(() =>
        {
            _ = new PreconditionAnalyzer().Analyze(context);
            _ = new PostconditionAnalyzer().AnalyzePostconditions(context);
            _ = new PostconditionAnalyzer().AnalyzeStateChanges(context);
            _ = new RuntimeRiskAnalyzer().Analyze(context);
            _ = new VariableAnalyzer().Analyze(context);
            _ = new DesignConstraintAnalyzer().Analyze(context);
            _ = new DependencyAnalyzer().Analyze(context);
            _ = new ExecutionFlowAnalyzer().Analyze(context);
        });

        Assert.Null(exception);
    }

    [Fact]
    public void ExecutionFlow_AlwaysProducesAtLeastOneNumberedStep()
    {
        // Both the detail panel and the exported documents render these as an ordered list, so an
        // empty or gapped sequence would show as a malformed section rather than an error.
        var context = Context(string.Empty);

        var steps = new ExecutionFlowAnalyzer().Analyze(context);

        Assert.NotEmpty(steps);
        Assert.Equal(Enumerable.Range(1, steps.Count), steps.Select(s => s.StepNumber));
    }
}

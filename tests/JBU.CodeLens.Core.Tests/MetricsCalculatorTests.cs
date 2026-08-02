using JBU.CodeLens.Core.Analysis;
using JBU.CodeLens.Shared.Structural;

namespace JBU.CodeLens.Core.Tests;

/// <summary>
/// Tests for the metrics whose derivation is non-trivial — in particular the maximum
/// inheritance depth, which walks INHERITS chains and must terminate on cycles. The depth
/// index optimization must preserve exactly the previous results.
/// </summary>
public class MetricsCalculatorTests
{
    private static ProjectIR IrWith(params (string Source, string Target)[] inherits)
    {
        var ir = new ProjectIR();
        var names = inherits.SelectMany(i => new[] { i.Source, i.Target }).Distinct();
        foreach (var name in names)
        {
            ir.Classes.Add(new TypeInfo { Name = name, FullName = name });
        }

        foreach (var (source, target) in inherits)
        {
            ir.Relationships.Add(new Relationship { SourceId = source, TargetId = target, Kind = "INHERITS" });
        }

        return ir;
    }

    [Fact]
    public void MaxInheritanceDepth_LinearChain_CountsEveryLevel()
    {
        // D -> C -> B -> A  is four levels deep.
        var metrics = MetricsCalculator.Calculate(
            IrWith(("D", "C"), ("C", "B"), ("B", "A")));

        Assert.Equal(4, metrics.MaxInheritanceDepth);
    }

    [Fact]
    public void MaxInheritanceDepth_NoInheritance_IsOne()
    {
        var ir = new ProjectIR();
        ir.Classes.Add(new TypeInfo { Name = "Solo", FullName = "Solo" });

        Assert.Equal(1, MetricsCalculator.Calculate(ir).MaxInheritanceDepth);
    }

    [Fact]
    public void MaxInheritanceDepth_CyclicInheritance_TerminatesInsteadOfHanging()
    {
        // A -> B -> A: a pathological cycle must not loop forever.
        var metrics = MetricsCalculator.Calculate(IrWith(("A", "B"), ("B", "A")));

        Assert.True(metrics.MaxInheritanceDepth >= 1);
    }

    [Fact]
    public void MaxInheritanceDepth_TakesDeepestOfSeveralChains()
    {
        var metrics = MetricsCalculator.Calculate(
            IrWith(("B", "A"), ("Z", "Y"), ("Y", "X"), ("X", "W")));

        Assert.Equal(4, metrics.MaxInheritanceDepth);
    }

    [Fact]
    public void CallsRelationships_DoNotAffectInheritanceDepth()
    {
        var ir = IrWith(("B", "A"));
        // A large number of CALLS edges must be ignored by the depth walk.
        for (var i = 0; i < 50; i++)
        {
            ir.Relationships.Add(new Relationship { SourceId = "B", TargetId = $"m{i}", Kind = "CALLS" });
        }

        Assert.Equal(2, MetricsCalculator.Calculate(ir).MaxInheritanceDepth);
    }

    /// <summary>Builds a project of one class holding methods of the given complexities.</summary>
    private static ProjectIR IrWithComplexity(bool documented, params int[] complexities)
    {
        var ir = new ProjectIR();
        var type = new TypeInfo { Name = "Sample", FullName = "Sample" };
        if (documented)
        {
            type.Documentation = new DocumentComment { Summary = "A documented class." };
        }

        foreach (var complexity in complexities)
        {
            var method = new MethodInfo
            {
                Name = $"M{complexity}",
                FullName = $"Sample.M{complexity}",
                CyclomaticComplexity = complexity,
            };
            type.Methods.Add(method);
            ir.Methods.Add(method);
        }

        ir.Classes.Add(type);
        return ir;
    }

    [Fact]
    public void Maintainability_RisesWhenTheCodeIsDocumented()
    {
        // The score used to fall as documentation was added, because the documented share was fed
        // into a term that is subtracted. Documenting the code must never make it look worse.
        var undocumented = MetricsCalculator.Calculate(IrWithComplexity(documented: false, 4, 4)).MaintainabilityIndex;
        var documented = MetricsCalculator.Calculate(IrWithComplexity(documented: true, 4, 4)).MaintainabilityIndex;

        Assert.True(
            documented > undocumented,
            $"documenting lowered the score: {undocumented} -> {documented}");
    }

    [Fact]
    public void Maintainability_FallsAsComplexityRises()
    {
        // The old formula was almost unmoved by complexity: an average of 100 still scored 83.
        var simple = MetricsCalculator.Calculate(IrWithComplexity(documented: true, 2, 2)).MaintainabilityIndex;
        var tangled = MetricsCalculator.Calculate(IrWithComplexity(documented: true, 40, 40)).MaintainabilityIndex;

        Assert.True(simple > tangled, $"complexity did not lower the score: {simple} vs {tangled}");
        Assert.True(tangled <= 30, $"a project this complex should score poorly, got {tangled}");
    }

    [Fact]
    public void Maintainability_StaysWithinTheRangeItClaims()
    {
        foreach (var ir in new[]
        {
            IrWithComplexity(documented: true, 1),
            IrWithComplexity(documented: false, 500),
            IrWithComplexity(documented: true, 1, 200, 3),
            new ProjectIR(),
        })
        {
            var score = MetricsCalculator.Calculate(ir).MaintainabilityIndex;
            Assert.InRange(score, 0, 100);
        }
    }
}

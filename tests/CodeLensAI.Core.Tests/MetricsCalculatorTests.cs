using CodeLensAI.Core.Analysis;
using CodeLensAI.Shared.Structural;

namespace CodeLensAI.Core.Tests;

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
        var metrics = new MetricsCalculator().Calculate(
            IrWith(("D", "C"), ("C", "B"), ("B", "A")));

        Assert.Equal(4, metrics.MaxInheritanceDepth);
    }

    [Fact]
    public void MaxInheritanceDepth_NoInheritance_IsOne()
    {
        var ir = new ProjectIR();
        ir.Classes.Add(new TypeInfo { Name = "Solo", FullName = "Solo" });

        Assert.Equal(1, new MetricsCalculator().Calculate(ir).MaxInheritanceDepth);
    }

    [Fact]
    public void MaxInheritanceDepth_CyclicInheritance_TerminatesInsteadOfHanging()
    {
        // A -> B -> A: a pathological cycle must not loop forever.
        var metrics = new MetricsCalculator().Calculate(IrWith(("A", "B"), ("B", "A")));

        Assert.True(metrics.MaxInheritanceDepth >= 1);
    }

    [Fact]
    public void MaxInheritanceDepth_TakesDeepestOfSeveralChains()
    {
        var metrics = new MetricsCalculator().Calculate(
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

        Assert.Equal(2, new MetricsCalculator().Calculate(ir).MaxInheritanceDepth);
    }
}

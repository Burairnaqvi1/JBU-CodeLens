using JBU.CodeLens.Core.Analysis;
using JBU.CodeLens.Core.Models;
using JBU.CodeLens.Shared.Structural;
using MethodInfo = JBU.CodeLens.Shared.Structural.MethodInfo;

namespace JBU.CodeLens.Core.Tests;

public class AnalysisTests
{
    [Fact]
    public void RelationshipExtractor_ExtractsInheritance()
    {
        var ir = new ProjectIR();
        ir.Classes.Add(new TypeInfo
        {
            Name = "Dog",
            FullName = "Animals.Dog",
            BaseTypes = new List<string> { "Animals.Animal" },
        });

        var rels = RelationshipExtractor.Extract(ir);

        Assert.Contains(rels, r => r.Kind == "INHERITS" && r.SourceId == "Animals.Dog" && r.TargetId == "Animals.Animal");
    }

    [Fact]
    public void MetricsCalculator_CalculatesCorrectCounts()
    {
        var ir = new ProjectIR();
        ir.Classes.Add(new TypeInfo { Name = "A", FullName = "A", Methods = { new MethodInfo { Name = "Foo", FullName = "A.Foo" } } });
        ir.Classes.Add(new TypeInfo { Name = "B", FullName = "B", Methods = { new MethodInfo { Name = "Bar", FullName = "B.Bar" } } });
        ir.Methods = ir.Classes.SelectMany(c => c.Methods).ToList();

        var metrics = MetricsCalculator.Calculate(ir);

        Assert.Equal(2, metrics.TotalClasses);
        Assert.Equal(2, metrics.TotalMethods);
        Assert.Equal(1, metrics.AverageMethodsPerClass);
    }

    [Fact]
    public void SymbolTable_BuildsFromProjectIR()
    {
        var ir = new ProjectIR();
        ir.Classes.Add(new TypeInfo { Name = "Player", FullName = "Game.Player", NamespaceName = "Game" });
        ir.Classes.Add(new TypeInfo { Name = "Monster", FullName = "Game.Monster", NamespaceName = "Game" });

        var table = new SymbolTable();
        table.BuildFrom(ir);

        Assert.NotNull(table.Lookup("Player"));
        Assert.NotNull(table.LookupFull("Game.Player"));
        Assert.NotNull(table.Lookup("Monster"));
        Assert.Null(table.Lookup("Nonexistent"));
    }
}

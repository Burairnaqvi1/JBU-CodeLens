using CodeLensAI.Core.Analysis;
using CodeLensAI.Core.Models;
using CodeLensAI.Shared.Models;
using CodeLensAI.Shared.Structural;
using MethodInfo = CodeLensAI.Shared.Structural.MethodInfo;
using Xunit;

namespace CodeLensAI.Tests;

public class GraphTests
{
    [Fact]
    public void BuildFrom_AddsNodesForClasses()
    {
        var ir = new ProjectIR();
        ir.ProjectName = "Test";
        ir.Classes.Add(new TypeInfo { Name = "Player", FullName = "Game.Player", NamespaceName = "Game" });
        ir.Namespaces.Add(new NamespaceInfo { Name = "Game", Classes = { ir.Classes[0] } });

        var graph = KnowledgeGraph.BuildFrom(ir);

        Assert.Contains(graph.Nodes, n => n.Key == "class:Game.Player");
        Assert.Contains(graph.Nodes, n => n.Key == "ns:Game");
    }

    [Fact]
    public void BuildFrom_AddsEdgesForRelationships()
    {
        var ir = new ProjectIR();
        ir.Relationships.Add(new Relationship
        {
            SourceId = "A",
            TargetId = "B",
            Kind = "CALLS",
        });

        var graph = KnowledgeGraph.BuildFrom(ir);

        Assert.Single(graph.Edges);
        Assert.Equal("CALLS", graph.Edges[0].Label);
    }

    [Fact]
    public void ToDictionary_ReturnsValidStructure()
    {
        var ir = new ProjectIR { ProjectName = "Test" };
        var graph = KnowledgeGraph.BuildFrom(ir);
        var dict = graph.ToDictionary();

        Assert.Equal("Test", dict["projectName"]);
        Assert.NotNull(dict["nodes"]);
        Assert.NotNull(dict["edges"]);
    }
}

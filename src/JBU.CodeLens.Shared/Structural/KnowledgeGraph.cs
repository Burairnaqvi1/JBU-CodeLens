namespace JBU.CodeLens.Shared.Structural;

public class GraphNode
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string Kind { get; set; } = "";
    public Dictionary<string, object> Properties { get; set; } = new();
}

public class GraphEdge
{
    public string SourceId { get; set; } = "";
    public string TargetId { get; set; } = "";
    public string Label { get; set; } = "";
}

public class KnowledgeGraph
{
    public Dictionary<string, GraphNode> Nodes { get; } = new();
    public List<GraphEdge> Edges { get; } = new();
    public string ProjectName { get; set; } = "";

    public void AddNode(GraphNode node)
    {
        if (!Nodes.ContainsKey(node.Id))
            Nodes[node.Id] = node;
    }

    public void AddEdge(GraphEdge edge) => Edges.Add(edge);

    public static KnowledgeGraph BuildFrom(ProjectIR ir)
    {
        var graph = new KnowledgeGraph { ProjectName = ir.ProjectName };

        foreach (var ns in ir.Namespaces)
        {
            graph.AddNode(new GraphNode { Id = $"ns:{ns.Name}", Label = ns.Name, Kind = "namespace" });
        }

        foreach (var cls in ir.Classes)
        {
            graph.AddNode(new GraphNode
            {
                Id = $"class:{cls.FullName}",
                Label = cls.Name,
                Kind = cls.Kind,
                Properties = new Dictionary<string, object>
                {
                    ["accessModifier"] = cls.AccessModifier,
                    ["methodCount"] = cls.Methods.Count,
                    ["propertyCount"] = cls.Properties.Count,
                },
            });

            if (!string.IsNullOrEmpty(cls.NamespaceName))
            {
                graph.AddEdge(new GraphEdge
                {
                    SourceId = $"ns:{cls.NamespaceName}",
                    TargetId = $"class:{cls.FullName}",
                    Label = "CONTAINS",
                });
            }
        }

        foreach (var rel in ir.Relationships)
        {
            graph.AddEdge(new GraphEdge { SourceId = rel.SourceId, TargetId = rel.TargetId, Label = rel.Kind });
        }

        return graph;
    }

    public Dictionary<string, object> ToDictionary()
    {
        var nodes = Nodes.Values.Select(n => new Dictionary<string, object>
        {
            ["id"] = n.Id,
            ["label"] = n.Label,
            ["kind"] = n.Kind,
            ["properties"] = n.Properties,
        }).ToList();

        var edges = Edges.Select(e => new Dictionary<string, object>
        {
            ["source"] = e.SourceId,
            ["target"] = e.TargetId,
            ["label"] = e.Label,
        }).ToList();

        return new Dictionary<string, object>
        {
            ["projectName"] = ProjectName,
            ["nodes"] = nodes,
            ["edges"] = edges,
        };
    }
}

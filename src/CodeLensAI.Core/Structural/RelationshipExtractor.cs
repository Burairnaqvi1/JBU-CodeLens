namespace CodeLensAI.Core.Structural;

public class RelationshipExtractor
{
    public List<Relationship> Extract(ProjectIR ir)
    {
        var relationships = new List<Relationship>();

        foreach (var cls in ir.Classes)
        {
            foreach (var baseType in cls.BaseTypes)
            {
                relationships.Add(new Relationship
                {
                    SourceId = cls.FullName,
                    TargetId = baseType,
                    Kind = "INHERITS",
                    SourceFile = cls.FilePath,
                });
            }

            foreach (var iface in cls.ImplementedInterfaces)
            {
                relationships.Add(new Relationship
                {
                    SourceId = cls.FullName,
                    TargetId = iface,
                    Kind = "IMPLEMENTS",
                    SourceFile = cls.FilePath,
                });
            }

            foreach (var method in cls.Methods)
            {
                foreach (var call in method.Calls)
                {
                    relationships.Add(new Relationship
                    {
                        SourceId = method.FullName,
                        TargetId = call,
                        Kind = "CALLS",
                        SourceFile = cls.FilePath,
                    });
                }
            }
        }

        return relationships;
    }
}

namespace CodeLensAI.Shared.Structural;

/// <summary>
/// Project-wide structural intermediate representation: aggregates every type discovered across
/// a scan, plus derived relationships, call graph, and metrics. Built once per scan by
/// <see cref="ScideEngine"/> from the same <see cref="ClassInfo"/> trees the UI tree uses, so a
/// project is only ever parsed once.
/// </summary>
public class ProjectIR
{
    public string ProjectName { get; set; } = "";
    public string RootPath { get; set; } = "";
    public int FilesAnalyzed { get; set; }
    public int FilesFailed { get; set; }

    public List<NamespaceInfo> Namespaces { get; set; } = new();
    public List<TypeInfo> Classes { get; set; } = new();
    public List<MethodInfo> Methods { get; set; } = new();

    public List<Relationship> Relationships { get; set; } = new();
    public Dictionary<string, List<string>> CallGraph { get; set; } = new();
    public Dictionary<string, TypeInfo> TypeIndex { get; set; } = new(StringComparer.Ordinal);
    public MetricsResult? Metrics { get; set; }
}

public class NamespaceInfo
{
    public string Name { get; set; } = "";
    public List<TypeInfo> Classes { get; set; } = new();
}

public class TypeInfo
{
    public string Name { get; set; } = "";
    public string FullName { get; set; } = "";
    public string NamespaceName { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string Kind { get; set; } = "class";
    public string AccessModifier { get; set; } = "public";

    public List<string> BaseTypes { get; set; } = new();
    public List<string> ImplementedInterfaces { get; set; } = new();
    public List<MethodInfo> Methods { get; set; } = new();
    public List<PropertyInfo> Properties { get; set; } = new();
    public List<FieldInfo> Fields { get; set; } = new();
    public DocumentComment? Documentation { get; set; }
}

public class MethodInfo
{
    public string Name { get; set; } = "";
    public string FullName { get; set; } = "";
    public string DeclaringType { get; set; } = "";
    public string ReturnType { get; set; } = "";
    public string AccessModifier { get; set; } = "private";

    public bool IsStatic { get; set; }
    public bool IsAbstract { get; set; }
    public bool IsVirtual { get; set; }
    public bool IsOverride { get; set; }
    public bool IsAsync { get; set; }

    public int CyclomaticComplexity { get; set; } = 1;
    public List<ParameterInfo> Parameters { get; set; } = new();
    public List<string> Calls { get; set; } = new();
    public DocumentComment? Documentation { get; set; }
}

public class ParameterInfo
{
    public string Name { get; set; } = "";
    public string TypeName { get; set; } = "";
}

public class PropertyInfo
{
    public string Name { get; set; } = "";
    public string TypeName { get; set; } = "";
    public string DeclaringType { get; set; } = "";
    public string AccessModifier { get; set; } = "private";
}

public class FieldInfo
{
    public string Name { get; set; } = "";
    public string TypeName { get; set; } = "";
    public string DeclaringType { get; set; } = "";
    public string AccessModifier { get; set; } = "private";
}

public class Relationship
{
    public string SourceId { get; set; } = "";
    public string TargetId { get; set; } = "";
    public string Kind { get; set; } = "";
    public string SourceFile { get; set; } = "";
}

public class Symbol
{
    public string Name { get; set; } = "";
    public string FullName { get; set; } = "";
    public string Kind { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string Namespace { get; set; } = "";
}

public class DocumentComment
{
    public string Summary { get; set; } = "";
}

public class MetricsResult
{
    public int TotalClasses { get; set; }
    public int TotalMethods { get; set; }
    public int TotalProperties { get; set; }
    public int TotalFields { get; set; }
    public int TotalNamespaces { get; set; }
    public int TotalRelationships { get; set; }

    public double AverageMethodsPerClass { get; set; }
    public double AveragePropertiesPerClass { get; set; }
    public double AverageComplexity { get; set; }
    public int MaxComplexity { get; set; }
    public int MaxInheritanceDepth { get; set; }
    public double AverageCoupling { get; set; }
    public double MaintainabilityIndex { get; set; }
}

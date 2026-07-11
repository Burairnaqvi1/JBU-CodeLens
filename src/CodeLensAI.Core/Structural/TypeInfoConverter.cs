using LensClass = CodeLensAI.Core.ClassInfo;

namespace CodeLensAI.Core.Structural;

/// <summary>
/// Converts an already-parsed <see cref="ClassInfo"/> tree into the project-wide
/// <see cref="TypeInfo"/> shape that <see cref="SymbolTable"/>, <see cref="RelationshipExtractor"/>,
/// <see cref="MetricsCalculator"/>, and <see cref="KnowledgeGraph"/> operate on. This is the only
/// place C#/C++ source ever gets parsed — <see cref="ScideEngine"/> reuses the same
/// <see cref="ClassInfo"/> trees the UI tree is built from instead of re-parsing.
/// </summary>
internal static class TypeInfoConverter
{
    public static TypeInfo FromClassInfo(LensClass cls)
    {
        var fullName = string.IsNullOrEmpty(cls.NamespaceName) ? cls.Name : $"{cls.NamespaceName}.{cls.Name}";

        var typeInfo = new TypeInfo
        {
            Name = cls.Name,
            FullName = fullName,
            NamespaceName = cls.NamespaceName,
            FilePath = cls.SourceFilePath,
            Kind = "class",
            AccessModifier = "public",
            BaseTypes = string.IsNullOrEmpty(cls.BaseClassName) ? [] : [cls.BaseClassName],
            ImplementedInterfaces = new List<string>(cls.ImplementedInterfaces),
            Documentation = string.IsNullOrWhiteSpace(cls.XmlSummary)
                ? null
                : new DocumentComment { Summary = cls.XmlSummary },
        };

        foreach (var field in cls.Fields)
        {
            typeInfo.Fields.Add(new FieldInfo
            {
                Name = field.Name,
                TypeName = field.Type,
                DeclaringType = fullName,
                AccessModifier = field.AccessModifier,
            });
        }

        foreach (var prop in cls.Properties)
        {
            typeInfo.Properties.Add(new PropertyInfo
            {
                Name = prop.Name,
                TypeName = prop.Type,
                DeclaringType = fullName,
                AccessModifier = prop.AccessModifier,
            });
        }

        foreach (var method in cls.Methods)
        {
            typeInfo.Methods.Add(new MethodInfo
            {
                Name = method.Name,
                FullName = $"{fullName}.{method.Name}",
                DeclaringType = fullName,
                ReturnType = method.ReturnType,
                AccessModifier = method.AccessModifier,
                CyclomaticComplexity = method.CyclomaticComplexity,
                Calls = new List<string>(method.CalledMethodNames),
                Documentation = string.IsNullOrWhiteSpace(method.XmlSummary)
                    ? null
                    : new DocumentComment { Summary = method.XmlSummary },
                Parameters = method.Parameters.Select(ToParameterInfo).ToList(),
            });
        }

        return typeInfo;
    }

    private static ParameterInfo ToParameterInfo(string parameter)
    {
        var parts = parameter.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        return new ParameterInfo
        {
            TypeName = parts.Length > 0 ? parts[0] : "var",
            Name = parts.Length > 1 ? parts[1] : "arg",
        };
    }
}

using CodeLensAI.Core.Analysis;
using LensMethod = CodeLensAI.Core.MethodInfo;
using LensClass = CodeLensAI.Core.ClassInfo;

namespace CodeLensAI.Core.Structural;

/// <summary>
/// Unified view model for the method detail panel. Merges CodeLensAI parser/inference data with
/// SCIDE project IR metadata (call targets, complexity) for the same method.
/// </summary>
public sealed class MethodDetailContext
{
    public required LensMethod Method { get; init; }
    public MethodInfo? ScideMethod { get; init; }
    public TypeInfo? ScideType { get; init; }
    public required MethodAnalysis Analysis { get; init; }
    public ProjectIR? ProjectIr { get; init; }

    public string? MergedXmlSummary =>
        !string.IsNullOrWhiteSpace(Method.XmlSummary)
            ? Method.XmlSummary.Trim()
            : ScideMethod?.Documentation?.Summary?.Trim();

    public IReadOnlyList<string> ScideCallTargets => ScideMethod?.Calls ?? [];

    public int ScideComplexity => ScideMethod?.CyclomaticComplexity ?? 0;

    public IReadOnlyList<string> ScideModifiers
    {
        get
        {
            if (ScideMethod is null) return [];
            var list = new List<string> { ScideMethod.AccessModifier };
            if (ScideMethod.IsStatic) list.Add("static");
            if (ScideMethod.IsAsync) list.Add("async");
            if (ScideMethod.IsVirtual) list.Add("virtual");
            if (ScideMethod.IsOverride) list.Add("override");
            if (ScideMethod.IsAbstract) list.Add("abstract");
            return list;
        }
    }
}

/// <summary>
/// Indexes SCIDE methods and types for lookup from CodeLensAI UI models.
/// </summary>
public static class ScideMethodIndex
{
    public static Dictionary<string, MethodInfo> BuildMethods(ProjectIR? ir)
    {
        var index = new Dictionary<string, MethodInfo>(StringComparer.OrdinalIgnoreCase);
        if (ir is null) return index;

        foreach (var type in ir.Classes)
        {
            foreach (var method in type.Methods)
            {
                index[method.FullName] = method;
                index[$"{type.Name}.{method.Name}"] = method;
                index[method.Name] = method;
            }
        }

        return index;
    }

    public static Dictionary<string, TypeInfo> BuildTypes(ProjectIR? ir)
    {
        var index = new Dictionary<string, TypeInfo>(StringComparer.OrdinalIgnoreCase);
        if (ir is null) return index;

        foreach (var type in ir.Classes)
        {
            if (!string.IsNullOrEmpty(type.FullName))
                index[type.FullName] = type;
            index[type.Name] = type;
        }

        return index;
    }

    public static MethodInfo? Lookup(
        IReadOnlyDictionary<string, MethodInfo> index,
        LensClass lensClass,
        LensMethod lensMethod)
    {
        var candidates = new[]
        {
            $"{lensClass.Name}.{lensMethod.Name}",
            lensMethod.Name,
        };

        foreach (var key in candidates)
        {
            if (index.TryGetValue(key, out var hit))
                return hit;
        }

        foreach (var (key, method) in index)
        {
            if (key.EndsWith($".{lensClass.Name}.{lensMethod.Name}", StringComparison.OrdinalIgnoreCase)
                || key.EndsWith($"{lensClass.Name}.{lensMethod.Name}", StringComparison.OrdinalIgnoreCase))
            {
                return method;
            }
        }

        return null;
    }

    public static TypeInfo? LookupType(
        IReadOnlyDictionary<string, TypeInfo> index,
        LensClass lensClass,
        MethodInfo? scideMethod)
    {
        if (scideMethod is not null && !string.IsNullOrEmpty(scideMethod.DeclaringType))
        {
            if (index.TryGetValue(scideMethod.DeclaringType, out var byDeclaring))
                return byDeclaring;
        }

        return index.TryGetValue(lensClass.Name, out var byName) ? byName : null;
    }

    public static MethodDetailContext Build(
        LensMethod lensMethod,
        ProjectIR? ir,
        IReadOnlyDictionary<string, MethodInfo> methodIndex,
        IReadOnlyDictionary<string, TypeInfo> typeIndex)
    {
        var parent = lensMethod.ParentClass;
        MethodInfo? scideMethod = parent is not null
            ? Lookup(methodIndex, parent, lensMethod)
            : null;
        TypeInfo? scideType = parent is not null
            ? LookupType(typeIndex, parent, scideMethod)
            : null;

        var analysis = lensMethod.CachedAnalysis ?? new InferenceEngine().Analyze(lensMethod);

        return new MethodDetailContext
        {
            Method = lensMethod,
            ScideMethod = scideMethod,
            ScideType = scideType,
            Analysis = analysis,
            ProjectIr = ir,
        };
    }
}

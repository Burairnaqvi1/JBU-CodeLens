using LensMethod = CodeLensAI.Shared.Models.MethodInfo;
using LensClass = CodeLensAI.Shared.Models.ClassInfo;

namespace CodeLensAI.Shared.Structural;

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

    /// <summary>
    /// One-line deterministic description of the method, precomputed by Core's context builder
    /// so renderers never invoke analysis logic directly.
    /// </summary>
    public string InferredDescription { get; init; } = string.Empty;

    /// <summary>
    /// The method's operational limits already formatted into readable sentences, index-aligned
    /// with <c>Method.OperationalLimits</c>. Precomputed by Core's context builder.
    /// </summary>
    public IReadOnlyList<string> FormattedOperationalLimits { get; init; } = [];

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

    // Note: context *construction* (which may run deterministic analysis as a fallback) lives
    // in Core — IProjectAnalyzer.BuildMethodDetailContext — so this shared assembly stays free
    // of analysis logic. Only pure index lookups live here.
}

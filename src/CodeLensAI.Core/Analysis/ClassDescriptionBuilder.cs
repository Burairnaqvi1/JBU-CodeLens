using System.Text;

namespace CodeLensAI.Core.Analysis;

/// <summary>
/// Produces a deterministic, plain-English one-line description of a class from facts the
/// parser already verified: its category, members, base types, and dependencies. Used as the
/// always-available fallback for "What This Class Does" when the class has no XML
/// documentation, and as the immediate text while an AI summary is still generating.
/// </summary>
public static class ClassDescriptionBuilder
{
    /// <summary>
    /// Builds a single readable sentence describing the class. Never throws and always
    /// returns a non-empty string.
    /// </summary>
    public static string Build(ClassInfo classInfo)
    {
        var name = classInfo.Name ?? string.Empty;
        var isInterface = LooksLikeInterface(name);

        var sb = new StringBuilder();
        sb.Append(DescribeRole(classInfo.Category));
        sb.Append(isInterface ? " interface" : " class");

        AppendMembers(classInfo, isInterface, sb);

        if (!string.IsNullOrEmpty(classInfo.BaseClassName))
        {
            sb.Append("; extends ").Append(classInfo.BaseClassName);
        }

        if (classInfo.ImplementedInterfaces.Count > 0)
        {
            sb.Append("; implements ").Append(JoinNames(classInfo.ImplementedInterfaces, max: 4));
        }

        if (classInfo.Dependencies.Count > 0)
        {
            sb.Append("; depends on ").Append(JoinNames(classInfo.Dependencies, max: 4));
        }

        sb.Append('.');
        return sb.ToString();
    }

    private static void AppendMembers(ClassInfo classInfo, bool isInterface, StringBuilder sb)
    {
        var methods = classInfo.Methods.Count;
        var properties = classInfo.Properties.Count;

        if (methods == 0 && properties == 0)
        {
            sb.Append(" with no members declared directly in it");
            return;
        }

        sb.Append(isInterface ? " defining " : " with ");

        if (methods > 0)
        {
            sb.Append(methods).Append(methods == 1 ? " method" : " methods");
            if (properties > 0)
            {
                sb.Append(" and ").Append(properties).Append(properties == 1 ? " property" : " properties");
            }
        }
        else
        {
            sb.Append(properties).Append(properties == 1 ? " property" : " properties");
        }

        if (methods > 0)
        {
            var names = classInfo.Methods.Select(m => m.Name).Where(n => !string.IsNullOrEmpty(n)).ToList();
            if (names.Count > 0)
            {
                sb.Append(", including ").Append(JoinNames(names, max: 3));
            }
        }
    }

    private static string DescribeRole(CodeCategory category) => category switch
    {
        CodeCategory.GuiLogic => "User-interface",
        CodeCategory.Utility => "Utility",
        _ => "Business-logic",
    };

    private static string JoinNames(IReadOnlyList<string> names, int max)
    {
        var shown = names.Take(max).ToList();
        var remaining = names.Count - shown.Count;

        var joined = shown.Count switch
        {
            1 => shown[0],
            2 when remaining == 0 => $"{shown[0]} and {shown[1]}",
            _ => remaining == 0
                ? $"{string.Join(", ", shown.Take(shown.Count - 1))}, and {shown[^1]}"
                : string.Join(", ", shown),
        };

        return remaining > 0 ? $"{joined}, and {remaining} more" : joined;
    }

    private static bool LooksLikeInterface(string name) =>
        name.Length >= 2 && name[0] == 'I' && char.IsUpper(name[1]);
}

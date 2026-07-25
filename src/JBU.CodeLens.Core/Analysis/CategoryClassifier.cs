namespace JBU.CodeLens.Core.Analysis;

/// <summary>
/// Assigns a <see cref="CodeCategory"/> to a class using simple, defensible heuristics based on
/// its name, base class, and shape (members and dependencies). The rules are intentionally
/// syntactic so they work without compiling the project.
/// </summary>
public static class CategoryClassifier
{
    /// <summary>
    /// Base class names that strongly indicate a WPF/XAML UI type. A class deriving from any of
    /// these is presentation code regardless of its own name.
    /// </summary>
    private static readonly string[] GuiBaseClasses =
    {
        "Window", "UserControl", "Page", "Control", "ContentControl",
    };

    /// <summary>
    /// Name suffixes that conventionally denote UI types (for example, <c>MainWindow</c>,
    /// <c>SettingsView</c>, <c>ConfirmDialog</c>).
    /// </summary>
    private static readonly string[] GuiNameSuffixes =
    {
        "Window", "View", "Control", "Dialog", "Page",
    };

    /// <summary>
    /// Name suffixes that conventionally denote utility/helper types.
    /// </summary>
    private static readonly string[] UtilityNameSuffixes =
    {
        "Helper", "Utility", "Utils", "Extensions",
    };

    /// <summary>
    /// Classifies <paramref name="classInfo"/> into a <see cref="CodeCategory"/>.
    /// </summary>
    /// <remarks>
    /// Rules are evaluated in priority order, first match wins:
    /// <list type="number">
    /// <item>
    /// <description>
    /// <b>GUI logic</b> — the class derives from a known UI base type (<c>Window</c>,
    /// <c>UserControl</c>, <c>Page</c>, <c>Control</c>, <c>ContentControl</c>) or its name ends
    /// with a UI suffix (<c>Window</c>, <c>View</c>, <c>Control</c>, <c>Dialog</c>, <c>Page</c>).
    /// UI is checked first because such a class is presentation code even if it also looks like a
    /// helper or holds business state.
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// <b>Utility</b> — the class name ends with a utility suffix (<c>Helper</c>, <c>Utility</c>,
    /// <c>Utils</c>, <c>Extensions</c>), or the class is shaped like a stateless function bag:
    /// no dependencies, no properties (so it carries no state), but at least one method (so it
    /// does provide behavior).
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// <b>Business logic</b> — the default when nothing above matches; assumed to model core
    /// domain behavior and data.
    /// </description>
    /// </item>
    /// </list>
    /// </remarks>
    /// <param name="classInfo">The class to classify.</param>
    /// <returns>The category the class falls into.</returns>
    public static CodeCategory Classify(ClassInfo classInfo)
    {
        if (IsGuiLogic(classInfo))
        {
            return CodeCategory.GuiLogic;
        }

        if (IsUtility(classInfo))
        {
            return CodeCategory.Utility;
        }

        return CodeCategory.BusinessLogic;
    }

    /// <summary>
    /// True when the class derives from a known UI base type or carries a UI name suffix.
    /// </summary>
    private static bool IsGuiLogic(ClassInfo classInfo)
    {
        if (classInfo.BaseClassName is not null &&
            GuiBaseClasses.Contains(classInfo.BaseClassName))
        {
            return true;
        }

        return EndsWithAny(classInfo.Name, GuiNameSuffixes);
    }

    /// <summary>
    /// True when the class name carries a utility suffix, or the class is a stateless function
    /// bag (no dependencies and no properties, but at least one method).
    /// </summary>
    private static bool IsUtility(ClassInfo classInfo)
    {
        if (EndsWithAny(classInfo.Name, UtilityNameSuffixes))
        {
            return true;
        }

        return classInfo.Dependencies.Count == 0 &&
               classInfo.Properties.Count == 0 &&
               classInfo.Methods.Count > 0;
    }

    /// <summary>
    /// Returns true when <paramref name="name"/> ends with any of the given suffixes
    /// (case-sensitive, matching C# type-naming conventions).
    /// </summary>
    private static bool EndsWithAny(string name, string[] suffixes)
    {
        foreach (var suffix in suffixes)
        {
            if (name.EndsWith(suffix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}

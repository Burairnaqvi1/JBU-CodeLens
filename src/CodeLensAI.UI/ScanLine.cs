using CodeLensAI.Core;

namespace CodeLensAI.UI;

/// <summary>
/// One row in the scan results list. Carries the display text plus an optional reference to the
/// <see cref="ClassInfo"/> the row belongs to, so that selecting a row (a class header or any of
/// its member/relationship lines) can be traced back to the class it describes. File-header and
/// error rows carry a <c>null</c> <see cref="ClassInfo"/> and are not explainable.
/// </summary>
public sealed class ScanLine
{
    /// <summary>The text shown in the list.</summary>
    public string Text { get; }

    /// <summary>
    /// The class this row is associated with, or <c>null</c> for non-class rows.
    /// </summary>
    public ClassInfo? ClassInfo { get; }

    /// <summary>Creates a scan line with optional associated class.</summary>
    public ScanLine(string text, ClassInfo? classInfo = null)
    {
        Text = text;
        ClassInfo = classInfo;
    }
}

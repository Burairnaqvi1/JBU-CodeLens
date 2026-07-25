namespace JBU.CodeLens.Core.Utilities;

/// <summary>
/// Helpers for reducing a declared type name to something readable.
/// </summary>
internal static class TypeNames
{
    /// <summary>
    /// Strips any namespace or C++ scope qualifier, leaving the bare type name:
    /// <c>System.Collections.List</c> and <c>std::vector</c> both reduce to their last segment.
    /// </summary>
    /// <remarks>
    /// The C++ scope separator is removed before the dot so that a name carrying both
    /// (<c>ns::Outer.Inner</c>) reduces the same way regardless of which appears last.
    /// </remarks>
    internal static string StripQualifiers(string type)
    {
        var simple = type;

        var scope = simple.LastIndexOf("::", StringComparison.Ordinal);
        if (scope >= 0)
        {
            simple = simple[(scope + 2)..];
        }

        var dot = simple.LastIndexOf('.');
        if (dot >= 0)
        {
            simple = simple[(dot + 1)..];
        }

        return simple;
    }
}

namespace JBU.CodeLens.Shared.Utilities;

/// <summary>
/// Lookups over the documentation tags a parser attached to a method.
/// </summary>
public static class MethodDocumentation
{
    private const string ExceptionTagPrefix = "exception:";

    /// <summary>
    /// Returns the documented description for <paramref name="exceptionType"/>, or <c>null</c>
    /// when the method documents no matching exception.
    /// </summary>
    /// <remarks>
    /// A suffix match is accepted as well as an exact one, because the parser records whatever the
    /// author wrote in the doc comment: a method documenting <c>System.ArgumentNullException</c>
    /// must still match a thrown type reported as <c>ArgumentNullException</c>.
    /// </remarks>
    public static string? FindExceptionDescription(Models.MethodInfo method, string exceptionType)
    {
        ArgumentNullException.ThrowIfNull(method);

        foreach (var tag in method.XmlDocTags)
        {
            if (!tag.Key.StartsWith(ExceptionTagPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var keyType = tag.Key[ExceptionTagPrefix.Length..];
            if (string.Equals(keyType, exceptionType, StringComparison.OrdinalIgnoreCase) ||
                keyType.EndsWith(exceptionType, StringComparison.OrdinalIgnoreCase))
            {
                return tag.Value;
            }
        }

        return null;
    }
}

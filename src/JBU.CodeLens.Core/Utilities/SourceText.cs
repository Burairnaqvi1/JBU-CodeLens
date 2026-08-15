using System.Text.RegularExpressions;

namespace JBU.CodeLens.Core.Utilities;

/// <summary>
/// Shared handling of raw source text, used by both parsers and by the analysers.
/// </summary>
internal static class SourceText
{
    /// <summary>
    /// Replaces comments and string literals with spaces, leaving everything else in place.
    /// </summary>
    /// <param name="text">The source to clean.</param>
    /// <param name="keepCharacterLiterals">
    /// Keep single-quoted literals. The rule that reads character ranges needs to see
    /// <c>'a'</c> and <c>'z'</c>; the counters that only look for keywords do not, and blanking
    /// them there avoids a stray quote confusing the match.
    /// </param>
    /// <remarks>
    /// <para>
    /// Everything removed is replaced by the same number of spaces rather than deleted, so every
    /// remaining character keeps its position. Quoted evidence therefore still lines up with the
    /// source, and offsets taken from the cleaned text stay valid against the original.
    /// </para>
    /// <para>
    /// Three copies of this pattern existed before, one in the limit analyser and two in the C++
    /// parser, differing only in whether character literals survived. That difference is a
    /// genuine requirement, so it is a parameter here rather than a reason to keep them apart.
    /// </para>
    /// </remarks>
    internal static string StripCommentsAndStrings(string text, bool keepCharacterLiterals)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var pattern = keepCharacterLiterals
            ? @"//[^\n]*|/\*.*?\*/|""(?:\\.|[^""\\\n])*""|'(?:\\.|[^'\\\n])*'"
            : @"//[^\n]*|/\*.*?\*/|""(?:\\.|[^""\\\n])*""";

        return SafeRegex.Replace(
            text,
            pattern,
            match => keepCharacterLiterals && match.Value[0] == '\''
                ? match.Value
                : new string(' ', match.Value.Length));
    }
}

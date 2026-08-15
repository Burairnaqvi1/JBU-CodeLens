using System.Text.RegularExpressions;

namespace JBU.CodeLens.Core.Utilities;

/// <summary>
/// Drop-in replacements for the static <see cref="Regex"/> helpers that always apply a match
/// timeout.
/// </summary>
/// <remarks>
/// <para>
/// Every pattern in this assembly runs over source files the user did not write. .NET's regex
/// engine backtracks, so a pathological or deliberately adversarial input can drive a match
/// exponentially and hang the scan with no way out and no error, the analysis simply never
/// returns. That is the ReDoS class of failure, and an unbounded match is the whole exposure.
/// </para>
/// <para>
/// A timeout converts an unbounded hang into a <see cref="RegexMatchTimeoutException"/>, which the
/// parsers' existing per-file error handling already records and recovers from: one hostile file
/// degrades to one reported parse error instead of freezing the application.
/// </para>
/// <para>
/// Two seconds is far beyond what any of these patterns need on a normal source file (they complete
/// in microseconds), so the timeout only ever fires on genuinely pathological input.
/// </para>
/// </remarks>
internal static class SafeRegex
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);

    internal static bool IsMatch(string input, string pattern) =>
        Regex.IsMatch(input, pattern, RegexOptions.None, MatchTimeout);

    internal static bool IsMatch(string input, string pattern, RegexOptions options) =>
        Regex.IsMatch(input, pattern, options, MatchTimeout);

    internal static Match Match(string input, string pattern) =>
        Regex.Match(input, pattern, RegexOptions.None, MatchTimeout);

    internal static Match Match(string input, string pattern, RegexOptions options) =>
        Regex.Match(input, pattern, options, MatchTimeout);

    internal static MatchCollection Matches(string input, string pattern) =>
        Regex.Matches(input, pattern, RegexOptions.None, MatchTimeout);

    internal static MatchCollection Matches(string input, string pattern, RegexOptions options) =>
        Regex.Matches(input, pattern, options, MatchTimeout);

    internal static string Replace(string input, string pattern, string replacement) =>
        Regex.Replace(input, pattern, replacement, RegexOptions.None, MatchTimeout);

    internal static string Replace(string input, string pattern, MatchEvaluator evaluator) =>
        Regex.Replace(input, pattern, evaluator, RegexOptions.None, MatchTimeout);
}

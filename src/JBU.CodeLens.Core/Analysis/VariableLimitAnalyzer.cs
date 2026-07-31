using System.Globalization;
using System.Text.RegularExpressions;

namespace JBU.CodeLens.Core.Analysis;

/// <summary>
/// Works out the range of values each variable is allowed to hold inside a method, by reading the
/// checks the method itself performs — guards that reject a value, calls that force it into a
/// range, comparisons the code relies on, and counting loops.
/// </summary>
/// <remarks>
/// <para>
/// Reads the method body text, which both parsers store, so one implementation covers C# and C++.
/// </para>
/// <para>
/// Every rule reports the line it read the limit from. A limit nobody can check is worse than no
/// limit at all, because the reader has no way to tell an inference from a fact.
/// </para>
/// <para>
/// Where two rules describe the same variable, the stronger evidence wins: a value a guard
/// rejects outright is a harder fact than one merely implied by the declared type. Only one limit
/// per variable is reported, so the reader is never asked to reconcile two claims.
/// </para>
/// </remarks>
public sealed class VariableLimitAnalyzer
{
    private readonly RuleEngine<VariableLimit> _engine;

    public VariableLimitAnalyzer()
    {
        _engine = new RuleEngine<VariableLimit>()
            .Register("limit-range-guard", "Value rejected outside a range", RuleRangeGuard)
            .Register("limit-clamp", "Value forced into a range", RuleClamp)
            .Register("limit-character-range", "Character restricted to a span", RuleCharacterRange)
            .Register("limit-comparison", "Bounds implied by comparisons", RuleComparisonBounds)
            .Register("limit-loop-bound", "Counter bounded by its loop", RuleLoopBound)
            .Register("limit-declared-type", "Range of the declared type", RuleDeclaredType);
    }

    public IReadOnlyList<AnalysisRule<VariableLimit>> Rules => _engine.Rules;

    /// <summary>
    /// Returns at most one limit per variable, strongest evidence first.
    /// </summary>
    public IReadOnlyList<VariableLimit> Analyze(MethodAnalysisContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.HasSourceBody)
        {
            return [];
        }

        // Rules are registered strongest-first, and the engine preserves that order, so the first
        // limit seen for a name is the best-evidenced one.
        var best = new Dictionary<string, VariableLimit>(StringComparer.Ordinal);
        foreach (var limit in _engine.EvaluateAll(context))
        {
            if (!best.ContainsKey(limit.Name))
            {
                best[limit.Name] = limit;
            }
        }

        return best.Values
            .OrderBy(l => l.Scope)
            .ThenBy(l => l.Name, StringComparer.Ordinal)
            .ToList();
    }

    // ── Rules ────────────────────────────────────────────────────────────────

    /// <summary>
    /// A check that rejects values outside a range, such as
    /// <c>if (level &lt; 0 || level &gt; 100) throw ...</c>. The guard names the values it
    /// refuses, so the permitted range is everything it does not refuse.
    /// </summary>
    private static IEnumerable<VariableLimit> RuleRangeGuard(MethodAnalysisContext context)
    {
        var body = context.SourceBody;
        var known = BuildDeclaredVariables(context);

        // name < LOW || name > HIGH   (and the >/>= mirror image)
        foreach (Match match in SafeRegex.Matches(
            body,
            @"\b([A-Za-z_]\w*)\s*<(=?)\s*" + NumberPattern + @"\s*\|\|\s*\1\s*>(=?)\s*" + NumberPattern))
        {
            var name = match.Groups[1].Value;
            if (!known.TryGetValue(name, out var declared)) continue;

            // "< 0" rejects everything below 0, so 0 itself is allowed; "<= 0" rejects 0 too.
            if (!TryParse(match.Groups[3].Value, out var lowValue)) continue;
            if (!TryParse(match.Groups[5].Value, out var highValue)) continue;

            var low = match.Groups[2].Value == "="
                ? Exclude(lowValue, raising: true)
                : new Bound(lowValue, Inclusive: true);
            var high = match.Groups[4].Value == "="
                ? Exclude(highValue, raising: false)
                : new Bound(highValue, Inclusive: true);
            if (low.Value > high.Value) continue;

            yield return new VariableLimit
            {
                Name = name,
                Type = declared.Type,
                Scope = declared.Scope,
                Limit = Describe(low, high),
                Evidence = Condense(match.Value),
                Source = VariableLimitSource.Guard,
                Confidence = AnalysisConfidence.High,
            };
        }
    }

    /// <summary>
    /// A call that forces a value into a range: <c>Math.Clamp(v, 1, 5)</c> in C#,
    /// <c>std::clamp(v, 1, 5)</c> in C++. The arguments state the range outright.
    /// </summary>
    private static IEnumerable<VariableLimit> RuleClamp(MethodAnalysisContext context)
    {
        var known = BuildDeclaredVariables(context);

        foreach (Match match in SafeRegex.Matches(
            context.SourceBody,
            @"(?:Math\.Clamp|std::clamp|clamp)\s*\(\s*([A-Za-z_]\w*)\s*,\s*"
                + NumberPattern + @"\s*,\s*" + NumberPattern + @"\s*\)"))
        {
            var name = match.Groups[1].Value;
            if (!known.TryGetValue(name, out var declared)) continue;
            if (!TryParse(match.Groups[2].Value, out var lowValue)) continue;
            if (!TryParse(match.Groups[3].Value, out var highValue)) continue;
            if (lowValue > highValue) continue;

            yield return new VariableLimit
            {
                Name = name,
                Type = declared.Type,
                Scope = declared.Scope,
                Limit = Describe(new Bound(lowValue, true), new Bound(highValue, true)),
                Evidence = Condense(match.Value),
                Source = VariableLimitSource.Clamp,
                Confidence = AnalysisConfidence.High,
            };
        }
    }

    /// <summary>
    /// A character restricted to a span, such as <c>c &gt;= 'a' &amp;&amp; c &lt;= 'z'</c>.
    /// Handled separately from the numeric rules so the range is shown as characters rather than
    /// as the numbers behind them.
    /// </summary>
    private static IEnumerable<VariableLimit> RuleCharacterRange(MethodAnalysisContext context)
    {
        var known = BuildDeclaredVariables(context);

        foreach (Match match in SafeRegex.Matches(
            context.SourceBody,
            @"\b([A-Za-z_]\w*)\s*>=\s*'(.)'\s*&&\s*\1\s*<=\s*'(.)'"))
        {
            var name = match.Groups[1].Value;
            if (!known.TryGetValue(name, out var declared)) continue;

            var low = match.Groups[2].Value;
            var high = match.Groups[3].Value;
            if (string.CompareOrdinal(low, high) > 0) continue;

            yield return new VariableLimit
            {
                Name = name,
                Type = declared.Type,
                Scope = declared.Scope,
                Limit = $"'{low}' to '{high}'",
                Evidence = Condense(match.Value),
                Source = VariableLimitSource.Guard,
                Confidence = AnalysisConfidence.High,
            };
        }
    }

    /// <summary>
    /// Bounds gathered from ordinary comparisons across the whole body — <c>n &gt;= 1</c> here,
    /// <c>n &lt;= 12</c> there. Weaker than a guard, because a comparison may only apply on one
    /// branch, so this reports only when both ends are found.
    /// </summary>
    private static IEnumerable<VariableLimit> RuleComparisonBounds(MethodAnalysisContext context)
    {
        var known = BuildDeclaredVariables(context);
        var lower = new Dictionary<string, (Bound Bound, string Evidence)>(StringComparer.Ordinal);
        var upper = new Dictionary<string, (Bound Bound, string Evidence)>(StringComparer.Ordinal);

        foreach (Match match in SafeRegex.Matches(
            context.SourceBody,
            @"\b([A-Za-z_]\w*)\s*(>=|<=|>|<)\s*" + NumberPattern + @"\b"))
        {
            var name = match.Groups[1].Value;
            if (!known.ContainsKey(name)) continue;
            if (!TryParse(match.Groups[3].Value, out var value)) continue;

            var evidence = Condense(match.Value);
            switch (match.Groups[2].Value)
            {
                case ">=": Widen(lower, name, new Bound(value, true), evidence, keepLower: true); break;
                case ">": Widen(lower, name, Exclude(value, raising: true), evidence, keepLower: true); break;
                case "<=": Widen(upper, name, new Bound(value, true), evidence, keepLower: false); break;
                case "<": Widen(upper, name, Exclude(value, raising: false), evidence, keepLower: false); break;
                default: break;
            }
        }

        foreach (var (name, low) in lower)
        {
            if (!upper.TryGetValue(name, out var high)) continue;
            if (low.Bound.Value > high.Bound.Value) continue;

            var declared = known[name];
            yield return new VariableLimit
            {
                Name = name,
                Type = declared.Type,
                Scope = declared.Scope,
                Limit = Describe(low.Bound, high.Bound),
                Evidence = $"{low.Evidence}, {high.Evidence}",
                Source = VariableLimitSource.Comparison,
                Confidence = AnalysisConfidence.Medium,
            };
        }
    }

    /// <summary>
    /// A counting loop states its own counter's range: <c>for (int i = 0; i &lt; 10; i++)</c>
    /// runs i from 0 to 9. Only reported when the end is a literal, since a variable end
    /// (<c>i &lt; items.Count</c>) gives no fixed number to show.
    /// </summary>
    private static IEnumerable<VariableLimit> RuleLoopBound(MethodAnalysisContext context)
    {
        var known = BuildDeclaredVariables(context);

        foreach (Match match in SafeRegex.Matches(
            context.SourceBody,
            @"for\s*\(\s*(?:[A-Za-z_][\w:<>,\s\*&]*\s+)?([A-Za-z_]\w*)\s*=\s*" + NumberPattern
                + @"\s*;\s*\1\s*(<=?)\s*" + NumberPattern + @"\s*;"))
        {
            var name = match.Groups[1].Value;
            if (!TryParse(match.Groups[2].Value, out var startValue)) continue;
            if (!TryParse(match.Groups[4].Value, out var endValue)) continue;

            var start = new Bound(startValue, Inclusive: true);
            var end = match.Groups[3].Value == "<"
                ? Exclude(endValue, raising: false)
                : new Bound(endValue, Inclusive: true);
            if (start.Value > end.Value) continue;

            // A loop counter declared in the for-statement is not in the parser's local list, so
            // fall back to describing it as a local rather than skipping it.
            var type = known.TryGetValue(name, out var declared) ? declared.Type : "int";
            var scope = known.TryGetValue(name, out var d2) ? d2.Scope : VariableScopeKind.Local;

            yield return new VariableLimit
            {
                Name = name,
                Type = type,
                Scope = scope,
                Limit = Describe(start, end),
                Evidence = Condense(match.Value.TrimEnd(';', ' ')),
                Source = VariableLimitSource.LoopBound,
                Confidence = AnalysisConfidence.Medium,
            };
        }
    }

    /// <summary>
    /// The range the declared type allows, for types that constrain their values meaningfully.
    /// Reported last and only when nothing narrower was found, so it never masks a real check.
    /// <c>int</c> and <c>double</c> are deliberately excluded: quoting their full range tells the
    /// reader nothing they did not already know.
    /// </summary>
    private static IEnumerable<VariableLimit> RuleDeclaredType(MethodAnalysisContext context)
    {
        foreach (var (name, declared) in BuildDeclaredVariables(context))
        {
            var range = DescribeTypeRange(declared.Type);
            if (range is null) continue;

            yield return new VariableLimit
            {
                Name = name,
                Type = declared.Type,
                Scope = declared.Scope,
                Limit = range,
                Evidence = $"declared as {declared.Type}",
                Source = VariableLimitSource.DeclaredType,
                Confidence = AnalysisConfidence.Low,
            };
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string? DescribeTypeRange(string type) =>
        NormaliseType(type) switch
        {
            "BOOL" or "BOOLEAN" => "true or false",
            "BYTE" or "UINT8_T" or "UNSIGNEDCHAR" => "0 to 255",
            "SBYTE" or "INT8_T" => "-128 to 127",
            "SHORT" or "INT16_T" => "-32,768 to 32,767",
            "USHORT" or "UINT16_T" or "UNSIGNEDSHORT" => "0 to 65,535",
            "UINT" or "UINT32_T" or "UNSIGNEDINT" => "0 or greater",
            "ULONG" or "UINT64_T" or "UNSIGNEDLONG" => "0 or greater",
            _ => null,
        };

    /// <summary>
    /// Strips the decorations a declaration may carry (const, reference, pointer, whitespace) so
    /// "const unsigned char&amp;" and "unsigned char" are recognised as the same type.
    /// </summary>
    private static string NormaliseType(string type)
    {
        var text = type.Replace("const", " ", StringComparison.Ordinal)
                       .Replace("*", " ", StringComparison.Ordinal)
                       .Replace("&", " ", StringComparison.Ordinal)
                       .Replace("std::", " ", StringComparison.Ordinal)
                       .Trim();

        // Upper rather than lower: uppercasing is the form recommended for comparison keys,
        // because lowercasing loses information for a few scripts.
        return string.Concat(text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                     .ToUpperInvariant();
    }

    /// <summary>
    /// Every variable the method declares or receives, indexed by name, so a rule can confirm a
    /// match is a real variable rather than a word that happens to appear in the text.
    /// </summary>
    private static Dictionary<string, (string Type, VariableScopeKind Scope)> BuildDeclaredVariables(
        MethodAnalysisContext context)
    {
        var map = new Dictionary<string, (string, VariableScopeKind)>(StringComparer.Ordinal);

        foreach (var parameter in context.Method.Parameters)
        {
            var parts = parameter.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            var name = parts[^1].Trim(',', '&', '*');
            map[name] = (string.Join(" ", parts[..^1]), VariableScopeKind.Parameter);
        }

        foreach (var local in context.Method.LocalVariables)
        {
            if (string.IsNullOrEmpty(local.Name)) continue;
            map[local.Name] = (local.Type, VariableScopeKind.Local);
        }

        foreach (var field in context.Method.ParentClass?.Fields ?? [])
        {
            if (string.IsNullOrEmpty(field.Name) || map.ContainsKey(field.Name)) continue;
            map[field.Name] = (field.Type, VariableScopeKind.Field);
        }

        return map;
    }

    /// <summary>Keeps the tightest bound seen, so several checks narrow rather than fight.</summary>
    private static void Widen(
        Dictionary<string, (Bound Bound, string Evidence)> bounds,
        string name,
        Bound bound,
        string evidence,
        bool keepLower)
    {
        if (!bounds.TryGetValue(name, out var existing))
        {
            bounds[name] = (bound, evidence);
            return;
        }

        var tighter = keepLower
            ? bound.Value > existing.Bound.Value
            : bound.Value < existing.Bound.Value;
        if (tighter)
        {
            bounds[name] = (bound, evidence);
        }
    }

    /// <summary>
    /// A single end of a range: the value, and whether the value itself is permitted.
    /// </summary>
    private readonly record struct Bound(decimal Value, bool Inclusive);

    /// <summary>
    /// The number in a literal, ignoring any type suffix C# or C++ may attach — <c>0m</c>,
    /// <c>1.5f</c>, <c>10L</c>. Written as a fragment so the rules can embed it in their patterns.
    /// </summary>
    private const string NumberPattern = @"(-?\d+(?:\.\d+)?)(?:[fFdDmMlLuU]{1,2})?";

    private static bool TryParse(string text, out decimal value) =>
        decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    /// <summary>
    /// Moves a bound past a value the code rejects. For whole numbers the neighbouring value is
    /// exact and reads better ("1 to 4" rather than "more than 0 and less than 5"); for anything
    /// fractional there is no neighbouring value, so the bound stays exclusive and is worded.
    /// </summary>
    private static Bound Exclude(decimal value, bool raising)
    {
        if (decimal.Truncate(value) == value)
        {
            return new Bound(raising ? value + 1 : value - 1, Inclusive: true);
        }

        return new Bound(value, Inclusive: false);
    }

    private static string Describe(Bound low, Bound high)
    {
        if (low.Inclusive && high.Inclusive)
        {
            return low.Value == high.Value
                ? string.Create(CultureInfo.InvariantCulture, $"exactly {Number(low.Value)}")
                : string.Create(CultureInfo.InvariantCulture, $"{Number(low.Value)} to {Number(high.Value)}");
        }

        var lowText = low.Inclusive
            ? string.Create(CultureInfo.InvariantCulture, $"{Number(low.Value)} or more")
            : string.Create(CultureInfo.InvariantCulture, $"more than {Number(low.Value)}");
        var highText = high.Inclusive
            ? string.Create(CultureInfo.InvariantCulture, $"{Number(high.Value)} or less")
            : string.Create(CultureInfo.InvariantCulture, $"less than {Number(high.Value)}");

        return $"{lowText}, {highText}";
    }

    /// <summary>Trims the trailing zeros a decimal keeps, so "1.50" reads as "1.5".</summary>
    private static string Number(decimal value) =>
        value.ToString("0.############################", CultureInfo.InvariantCulture);

    /// <summary>Collapses runs of whitespace so quoted evidence stays on one line.</summary>
    private static string Condense(string text) =>
        string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

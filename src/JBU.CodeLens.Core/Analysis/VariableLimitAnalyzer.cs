using System.Globalization;
using System.Text.RegularExpressions;

namespace JBU.CodeLens.Core.Analysis;

/// <summary>
/// Works out the range of values each variable is allowed to hold inside a method — its
/// operation limit — by reading the checks the method performs on it.
/// </summary>
/// <remarks>
/// <para>
/// Every variable gets an answer. Where the code restricts a value the limit says so
/// ("1 to 100", "greater than 0", "at most 10 characters"); where it does not, the limit falls
/// back to what the declared type permits ("any whole number"). A blank entry would leave the
/// reader unable to tell "unrestricted" from "not examined".
/// </para>
/// <para>
/// Reads the method body text, which both parsers store, so one implementation covers C# and C++.
/// </para>
/// <para>
/// The central distinction is between a guard and a comparison. A guard that throws names the
/// values it <em>rejects</em>, so the permitted range is its opposite: <c>if (n &lt;= 0) throw</c>
/// permits "greater than 0". A plain comparison names the values the code <em>relies on</em>, so
/// it is taken at face value: <c>if (n &gt; 0) Use(n)</c> also permits "greater than 0", but by
/// the opposite reasoning. Getting these the same way round would invert half the results.
/// </para>
/// <para>
/// Every rule reports the line it read the limit from, and where two rules describe the same
/// variable the stronger evidence wins, so the reader is never asked to reconcile two claims.
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
            .Register("limit-length-guard", "Length rejected outside a bound", RuleLengthGuard)
            .Register("limit-single-guard", "Value rejected beyond one bound", RuleSingleBoundGuard)
            .Register("limit-not-null", "Value rejected when absent", RuleNotNull)
            .Register("limit-comparison", "Bounds the code relies on", RuleComparisonBounds)
            .Register("limit-loop-bound", "Counter bounded by its loop", RuleLoopBound)
            .Register("limit-declared-type", "Range the declared type permits", RuleDeclaredType);
    }

    public IReadOnlyList<AnalysisRule<VariableLimit>> Rules => _engine.Rules;

    /// <summary>
    /// Returns exactly one limit per variable, from the strongest evidence available.
    /// </summary>
    public IReadOnlyList<VariableLimit> Analyze(MethodAnalysisContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Rules are registered strongest-first and the engine preserves that order, so the first
        // limit seen for a name is the best-evidenced one.
        var best = new Dictionary<string, VariableLimit>(StringComparer.Ordinal);
        foreach (var limit in _engine.EvaluateAll(context))
        {
            if (!string.IsNullOrEmpty(limit.Name) && !best.ContainsKey(limit.Name))
            {
                best[limit.Name] = limit;
            }
        }

        return best.Values
            .OrderBy(l => l.Scope)
            .ThenBy(l => l.Name, StringComparer.Ordinal)
            .ToList();
    }

    // ── Rules: the code restricts the value ──────────────────────────────────

    /// <summary>
    /// A guard rejecting values outside a range: <c>if (level &lt; 0 || level &gt; 100) throw</c>.
    /// </summary>
    private static IEnumerable<VariableLimit> RuleRangeGuard(MethodAnalysisContext context)
    {
        var known = BuildDeclaredVariables(context);

        foreach (var line in GuardLines(context))
        {
            foreach (Match match in SafeRegex.Matches(
                line,
                @"\b([A-Za-z_]\w*)\s*<(=?)\s*" + NumberPattern + @"\s*\|\|\s*\1\s*>(=?)\s*" + NumberPattern))
            {
                var name = match.Groups[1].Value;
                if (!known.TryGetValue(name, out var declared)) continue;
                if (!TryParse(match.Groups[3].Value, out var lowValue)) continue;
                if (!TryParse(match.Groups[5].Value, out var highValue)) continue;

                // The guard states what is refused, so "< 0" leaves 0 itself permitted while
                // "<= 0" refuses 0 as well.
                var low = match.Groups[2].Value == "="
                    ? Exclude(lowValue, raising: true)
                    : new Bound(lowValue, Inclusive: true);
                var high = match.Groups[4].Value == "="
                    ? Exclude(highValue, raising: false)
                    : new Bound(highValue, Inclusive: true);
                if (low.Value > high.Value) continue;

                yield return Build(name, declared, DescribeRange(low, high), match.Value,
                    VariableLimitSource.Guard, AnalysisConfidence.High);
            }
        }
    }

    /// <summary>
    /// A guard rejecting values beyond a single bound: <c>if (count &lt;= 0) throw</c> permits
    /// "greater than 0". This is the commonest restriction in real code and the reason a
    /// one-sided bound is worth reporting rather than waiting for a matching pair.
    /// </summary>
    private static IEnumerable<VariableLimit> RuleSingleBoundGuard(MethodAnalysisContext context)
    {
        var known = BuildDeclaredVariables(context);

        foreach (var line in GuardLines(context))
        {
            foreach (Match match in SafeRegex.Matches(
                line, @"\b([A-Za-z_]\w*)\s*(>=|<=|>|<)\s*" + NumberPattern + @"\b"))
            {
                var name = match.Groups[1].Value;
                if (!known.TryGetValue(name, out var declared)) continue;
                if (IsLengthExpression(line, name)) continue;
                if (!TryParse(match.Groups[3].Value, out var value)) continue;

                // Rejected "< 5" leaves "5 or more" permitted; rejected "<= 5" leaves
                // "greater than 5". The operator flips because the guard describes refusals.
                var text = match.Groups[2].Value switch
                {
                    "<" => DescribeLower(new Bound(value, Inclusive: true)),
                    "<=" => DescribeLower(new Bound(value, Inclusive: false)),
                    ">" => DescribeUpper(new Bound(value, Inclusive: true)),
                    _ => DescribeUpper(new Bound(value, Inclusive: false)),
                };

                yield return Build(name, declared, text, match.Value,
                    VariableLimitSource.Guard, AnalysisConfidence.High);
            }
        }
    }

    /// <summary>
    /// A guard on how much a value may hold: <c>if (name.Length &gt; 50) throw</c> permits
    /// "at most 50 characters". Counted in characters for text and in items for a collection.
    /// </summary>
    private static IEnumerable<VariableLimit> RuleLengthGuard(MethodAnalysisContext context)
    {
        var known = BuildDeclaredVariables(context);

        foreach (var line in GuardLines(context))
        {
            foreach (Match match in SafeRegex.Matches(
                line,
                @"\b([A-Za-z_]\w*)\s*(?:\.\s*(?:Length|Count|Size)\b|\.\s*(?:length|size)\s*\(\s*\))\s*(>=|<=|>|<)\s*"
                    + NumberPattern))
            {
                var name = match.Groups[1].Value;
                if (!known.TryGetValue(name, out var declared)) continue;
                if (!TryParse(match.Groups[3].Value, out var value)) continue;

                var unit = UnitFor(declared.Type);

                // Rejecting "> 50" permits up to 50; rejecting ">= 50" permits up to 49.
                var text = match.Groups[2].Value switch
                {
                    ">" => AtMost(value, unit),
                    ">=" => AtMost(value - 1, unit),
                    "<" => AtLeast(value, unit),
                    _ => AtLeast(value + 1, unit),
                };

                yield return Build(name, declared, text, match.Value,
                    VariableLimitSource.Guard, AnalysisConfidence.High);
            }
        }
    }

    /// <summary>
    /// A guard rejecting an absent value: <c>if (order == null) throw</c>. Not a range, but it is
    /// the operating limit on a reference, and leaving it unstated would be a gap.
    /// </summary>
    private static IEnumerable<VariableLimit> RuleNotNull(MethodAnalysisContext context)
    {
        var known = BuildDeclaredVariables(context);

        foreach (var line in GuardLines(context))
        {
            foreach (Match match in SafeRegex.Matches(
                line,
                @"\b([A-Za-z_]\w*)\s*==\s*(?:null|nullptr)\b|(?:IsNullOrEmpty|IsNullOrWhiteSpace)\s*\(\s*([A-Za-z_]\w*)\s*\)"))
            {
                var name = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
                if (string.IsNullOrEmpty(name) || !known.TryGetValue(name, out var declared)) continue;

                var text = match.Groups[2].Success && IsTextType(declared.Type)
                    ? "must not be empty"
                    : "must not be null";

                yield return Build(name, declared, text, match.Value,
                    VariableLimitSource.Guard, AnalysisConfidence.High);
            }
        }
    }

    /// <summary>
    /// A call that forces a value into a range: <c>Math.Clamp(v, 1, 5)</c> in C#,
    /// <c>std::clamp(v, 1, 5)</c> in C++.
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
            if (!TryParse(match.Groups[2].Value, out var low)) continue;
            if (!TryParse(match.Groups[3].Value, out var high)) continue;
            if (low > high) continue;

            yield return Build(
                name, declared,
                DescribeRange(new Bound(low, true), new Bound(high, true)), match.Value,
                VariableLimitSource.Clamp, AnalysisConfidence.High);
        }
    }

    /// <summary>
    /// A character restricted to a span: <c>c &gt;= 'a' &amp;&amp; c &lt;= 'z'</c>. Shown as
    /// characters rather than the numbers behind them.
    /// </summary>
    private static IEnumerable<VariableLimit> RuleCharacterRange(MethodAnalysisContext context)
    {
        var known = BuildDeclaredVariables(context);

        foreach (Match match in SafeRegex.Matches(
            context.SourceBody, @"\b([A-Za-z_]\w*)\s*>=\s*'(.)'\s*&&\s*\1\s*<=\s*'(.)'"))
        {
            var name = match.Groups[1].Value;
            if (!known.TryGetValue(name, out var declared)) continue;

            var low = match.Groups[2].Value;
            var high = match.Groups[3].Value;
            if (string.CompareOrdinal(low, high) > 0) continue;

            yield return Build(name, declared, $"'{low}' to '{high}'", match.Value,
                VariableLimitSource.Guard, AnalysisConfidence.High);
        }
    }

    /// <summary>
    /// Bounds gathered from ordinary comparisons anywhere in the body. Taken at face value: a
    /// comparison the code branches on describes values it works with, not values it refuses.
    /// Weaker than a guard because it may hold on only one branch.
    /// </summary>
    private static IEnumerable<VariableLimit> RuleComparisonBounds(MethodAnalysisContext context)
    {
        var known = BuildDeclaredVariables(context);
        var lower = new Dictionary<string, (Bound Bound, string Evidence)>(StringComparer.Ordinal);
        var upper = new Dictionary<string, (Bound Bound, string Evidence)>(StringComparer.Ordinal);

        foreach (Match match in SafeRegex.Matches(
            context.SourceBody, @"\b([A-Za-z_]\w*)\s*(>=|<=|>|<)\s*" + NumberPattern + @"\b"))
        {
            var name = match.Groups[1].Value;
            if (!known.ContainsKey(name)) continue;
            if (!TryParse(match.Groups[3].Value, out var value)) continue;

            var evidence = Condense(match.Value);
            switch (match.Groups[2].Value)
            {
                case ">=": Narrow(lower, name, new Bound(value, true), evidence, isLower: true); break;
                case ">": Narrow(lower, name, new Bound(value, false), evidence, isLower: true); break;
                case "<=": Narrow(upper, name, new Bound(value, true), evidence, isLower: false); break;
                case "<": Narrow(upper, name, new Bound(value, false), evidence, isLower: false); break;
                default: break;
            }
        }

        foreach (var name in lower.Keys.Union(upper.Keys, StringComparer.Ordinal))
        {
            var hasLow = lower.TryGetValue(name, out var low);
            var hasHigh = upper.TryGetValue(name, out var high);
            var declared = known[name];

            if (hasLow && hasHigh)
            {
                if (low.Bound.Value > high.Bound.Value) continue;
                yield return Build(
                    name, declared, DescribeRange(low.Bound, high.Bound),
                    $"{low.Evidence}, {high.Evidence}",
                    VariableLimitSource.Comparison, AnalysisConfidence.Medium);
            }
            else if (hasLow)
            {
                yield return Build(name, declared, DescribeLower(low.Bound), low.Evidence,
                    VariableLimitSource.Comparison, AnalysisConfidence.Medium);
            }
            else
            {
                yield return Build(name, declared, DescribeUpper(high.Bound), high.Evidence,
                    VariableLimitSource.Comparison, AnalysisConfidence.Medium);
            }
        }
    }

    /// <summary>
    /// A counting loop states its own counter's range: <c>for (int i = 0; i &lt; 10; i++)</c>
    /// runs i from 0 to 9. Reported only when the end is a literal, since a variable end gives no
    /// fixed number to show.
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

            // A counter declared inside the for-statement is not in the parser's local list, so
            // describe it as a local rather than dropping it.
            var declared = known.TryGetValue(name, out var d)
                ? d
                : ("int", VariableScopeKind.Local);

            yield return Build(name, declared, DescribeRange(start, end),
                match.Value.TrimEnd(';', ' '),
                VariableLimitSource.LoopBound, AnalysisConfidence.Medium);
        }
    }

    // ── Rule: nothing restricts it, so the type has the last word ────────────

    /// <summary>
    /// What the declared type permits, reported for every variable so none is left blank. Runs
    /// last, so it only ever describes variables no check restricted.
    /// </summary>
    private static IEnumerable<VariableLimit> RuleDeclaredType(MethodAnalysisContext context)
    {
        foreach (var (name, declared) in BuildDeclaredVariables(context))
        {
            yield return Build(name, declared, DescribeTypeRange(declared.Type),
                $"declared as {declared.Type}",
                VariableLimitSource.DeclaredType, AnalysisConfidence.Low);
        }
    }

    // ── Reading the source ───────────────────────────────────────────────────

    /// <summary>
    /// The lines that reject a value — those combining a test with a <c>throw</c> or an early
    /// <c>return</c>. Splitting first keeps one line's condition from pairing with another's
    /// bound, which whole-body matching would allow.
    /// </summary>
    private static IEnumerable<string> GuardLines(MethodAnalysisContext context)
    {
        foreach (var raw in context.SourceBody.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Contains("if", StringComparison.Ordinal) &&
                (line.Contains("throw", StringComparison.Ordinal) ||
                 line.Contains("return", StringComparison.Ordinal)))
            {
                yield return line;
            }
        }
    }

    /// <summary>
    /// True when the comparison on this line is about how much the variable holds rather than its
    /// value, so the numeric rules leave it to the length rule.
    /// </summary>
    private static bool IsLengthExpression(string line, string name) =>
        SafeRegex.IsMatch(line, $@"\b{Regex.Escape(name)}\s*\.\s*(?:Length|Count|Size|length|size)\b");

    /// <summary>
    /// Every variable the method declares or receives. Class fields are included only when the
    /// body mentions them, so a method is not padded with fields it never touches.
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
            if (name.Length > 0)
            {
                map[name] = (string.Join(" ", parts[..^1]), VariableScopeKind.Parameter);
            }
        }

        foreach (var local in context.Method.LocalVariables)
        {
            if (string.IsNullOrEmpty(local.Name)) continue;
            map[local.Name] = (local.Type, VariableScopeKind.Local);
        }

        foreach (var field in context.Method.ParentClass?.Fields ?? [])
        {
            if (string.IsNullOrEmpty(field.Name) || map.ContainsKey(field.Name)) continue;
            if (!context.SourceBody.Contains(field.Name, StringComparison.Ordinal)) continue;
            map[field.Name] = (field.Type, VariableScopeKind.Field);
        }

        return map;
    }

    // ── Wording ──────────────────────────────────────────────────────────────

    /// <summary>
    /// What the declared type permits, in words. Exact ranges for the narrow numeric types, and a
    /// plain description for the wide ones — quoting the full span of an <c>int</c> tells the
    /// reader nothing and crowds out the variables that carry a real restriction.
    /// </summary>
    private static string DescribeTypeRange(string type)
    {
        var normalised = NormaliseType(type);
        return normalised switch
        {
            "BOOL" or "BOOLEAN" => "true or false",
            "BYTE" or "UINT8_T" or "UNSIGNEDCHAR" => "0 to 255",
            "SBYTE" or "INT8_T" => "-128 to 127",
            "SHORT" or "INT16_T" => "-32,768 to 32,767",
            "USHORT" or "UINT16_T" or "UNSIGNEDSHORT" => "0 to 65,535",
            // size_t is unsigned, so it belongs with the types that cannot go below zero.
            "UINT" or "UINT32_T" or "UNSIGNEDINT" or "ULONG" or "UINT64_T" or "UNSIGNEDLONG"
                or "SIZE_T"
                => "0 or greater",
            "INT" or "LONG" or "INT32_T" or "INT64_T" or "LONGLONG"
                => "any whole number",
            "FLOAT" or "DOUBLE" or "DECIMAL" => "any decimal number",
            "CHAR" or "WCHAR_T" => "any single character",
            "STRING" or "STRING_VIEW" => "any text",
            "DATETIME" => "any date and time",
            _ => IsCollectionType(type) ? "any number of items" : $"any {type.Trim()} value",
        };
    }

    private static string UnitFor(string type) =>
        IsTextType(type) ? "characters" : "items";

    private static bool IsTextType(string type) =>
        NormaliseType(type) is "STRING" or "STRING_VIEW" or "CHAR*" or "CONSTCHAR*";

    private static bool IsCollectionType(string type) =>
        type.Contains("[]", StringComparison.Ordinal) ||
        type.Contains("List<", StringComparison.Ordinal) ||
        type.Contains("vector<", StringComparison.Ordinal) ||
        type.Contains("IEnumerable", StringComparison.Ordinal) ||
        type.Contains("ICollection", StringComparison.Ordinal) ||
        type.Contains("Dictionary", StringComparison.Ordinal) ||
        type.Contains("map<", StringComparison.Ordinal) ||
        type.Contains("set<", StringComparison.Ordinal);

    private static string AtMost(decimal value, string unit) =>
        string.Create(CultureInfo.InvariantCulture, $"at most {Number(value)} {unit}");

    private static string AtLeast(decimal value, string unit) =>
        string.Create(CultureInfo.InvariantCulture, $"at least {Number(value)} {unit}");

    private static string DescribeRange(Bound low, Bound high)
    {
        if (low.Inclusive && high.Inclusive)
        {
            return low.Value == high.Value
                ? string.Create(CultureInfo.InvariantCulture, $"exactly {Number(low.Value)}")
                : string.Create(CultureInfo.InvariantCulture, $"{Number(low.Value)} to {Number(high.Value)}");
        }

        return $"{DescribeLower(low)}, {DescribeUpper(high)}";
    }

    private static string DescribeLower(Bound bound) =>
        bound.Inclusive
            ? string.Create(CultureInfo.InvariantCulture, $"{Number(bound.Value)} or greater")
            : string.Create(CultureInfo.InvariantCulture, $"greater than {Number(bound.Value)}");

    private static string DescribeUpper(Bound bound) =>
        bound.Inclusive
            ? string.Create(CultureInfo.InvariantCulture, $"{Number(bound.Value)} or less")
            : string.Create(CultureInfo.InvariantCulture, $"less than {Number(bound.Value)}");

    // ── Numbers ──────────────────────────────────────────────────────────────

    /// <summary>One end of a range: the value, and whether the value itself is permitted.</summary>
    private readonly record struct Bound(decimal Value, bool Inclusive);

    /// <summary>
    /// The number in a literal, ignoring any type suffix C# or C++ may attach — <c>0m</c>,
    /// <c>1.5f</c>, <c>10L</c>.
    /// </summary>
    private const string NumberPattern = @"(-?\d+(?:\.\d+)?)(?:[fFdDmMlLuU]{1,2})?";

    private static bool TryParse(string text, out decimal value) =>
        decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    /// <summary>
    /// Steps a bound past a rejected value. Whole numbers have a neighbour, so the result stays
    /// exact and reads better ("1 to 4"); fractions have none, so the bound stays exclusive and
    /// the wording carries the meaning.
    /// </summary>
    private static Bound Exclude(decimal value, bool raising)
    {
        if (decimal.Truncate(value) != value)
        {
            return new Bound(value, Inclusive: false);
        }

        var neighbour = raising ? value + 1 : value - 1;
        return new Bound(neighbour, Inclusive: true);
    }

    /// <summary>Trims the trailing zeros a decimal keeps, so "1.50" reads as "1.5".</summary>
    private static string Number(decimal value) =>
        value.ToString("0.############################", CultureInfo.InvariantCulture);

    // ── Shared plumbing ──────────────────────────────────────────────────────

    private static VariableLimit Build(
        string name,
        (string Type, VariableScopeKind Scope) declared,
        string limit,
        string evidence,
        VariableLimitSource source,
        AnalysisConfidence confidence) =>
        new()
        {
            Name = name,
            Type = declared.Type,
            Scope = declared.Scope,
            Limit = limit,
            Evidence = Condense(evidence),
            Source = source,
            Confidence = confidence,
        };

    /// <summary>Keeps the tightest bound seen, so several checks narrow rather than fight.</summary>
    private static void Narrow(
        Dictionary<string, (Bound Bound, string Evidence)> bounds,
        string name,
        Bound bound,
        string evidence,
        bool isLower)
    {
        if (!bounds.TryGetValue(name, out var existing))
        {
            bounds[name] = (bound, evidence);
            return;
        }

        var tighter = isLower
            ? bound.Value > existing.Bound.Value
            : bound.Value < existing.Bound.Value;
        if (tighter)
        {
            bounds[name] = (bound, evidence);
        }
    }

    /// <summary>
    /// Strips the decorations a declaration may carry (const, reference, pointer, whitespace) so
    /// "const unsigned char&amp;" and "unsigned char" are recognised as the same type. Upper
    /// rather than lower because uppercasing is the form recommended for comparison keys.
    /// </summary>
    private static string NormaliseType(string type)
    {
        var text = type.Replace("const", " ", StringComparison.Ordinal)
                       .Replace("&", " ", StringComparison.Ordinal)
                       .Replace("std::", " ", StringComparison.Ordinal)
                       .Trim();

        return string.Concat(text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                     .ToUpperInvariant();
    }

    /// <summary>Collapses runs of whitespace so quoted evidence stays on one line.</summary>
    private static string Condense(string text) =>
        string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

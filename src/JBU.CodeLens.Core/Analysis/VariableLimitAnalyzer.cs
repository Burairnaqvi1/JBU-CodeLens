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
/// Every rule reports the line it read the limit from. Where two rules make rival claims about
/// the same aspect of a variable, the better-evidenced one wins; where they describe different
/// aspects — being present, and being short — both are true and are joined into one statement.
/// </para>
/// <para>
/// Comments and string literals are blanked out before any of this. A limit read from a
/// commented-out line is worse than no limit, because it states a restriction the running code
/// does not apply.
/// </para>
/// </remarks>
public sealed class VariableLimitAnalyzer
{
    private readonly RuleEngine<VariableLimit> _engine;

    public VariableLimitAnalyzer()
    {
        _engine = new RuleEngine<VariableLimit>()
            .Register("limit-range-guard", "Value rejected outside a range", RuleRangeGuard)
            .Register("limit-allowed-values", "Only a few values admitted", RuleAllowedValues)
            .Register("limit-clamp", "Value forced into a range", RuleClamp)
            .Register("limit-character-range", "Character restricted to a span", RuleCharacterRange)
            .Register("limit-symbolic-range", "Range bounded by other variables", RuleSymbolicRangeGuard)
            .Register("limit-length-guard", "Length rejected outside a bound", RuleLengthGuard)
            .Register("limit-single-guard", "Value rejected beyond one bound", RuleSingleBoundGuard)
            .Register("limit-not-null", "Value rejected when absent", RuleNotNull)
            .Register("limit-divisor", "Value used as a divisor", RuleDivisor)
            .Register("limit-comparison", "Bounds the code relies on", RuleComparisonBounds)
            .Register("limit-loop-bound", "Counter bounded by its loop", RuleLoopBound)
            .Register("limit-declared-type", "Range the declared type permits", RuleDeclaredType);
    }

    public IReadOnlyList<AnalysisRule<VariableLimit>> Rules => _engine.Rules;

    /// <summary>
    /// Returns one limit per variable, combining everything known about it.
    /// </summary>
    /// <remarks>
    /// Two findings of the same kind are rival claims about one thing, so only the
    /// better-evidenced survives — rules run strongest-first, so that is simply the first seen.
    /// Findings of different kinds are complementary and are joined: a string required to be
    /// present and no longer than fifty characters reads "must not be null, at most 50
    /// characters", rather than the reader being shown half of what the code enforces.
    /// </remarks>
    public IReadOnlyList<VariableLimit> Analyze(MethodAnalysisContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var byVariable = new Dictionary<string, Dictionary<VariableLimitKind, VariableLimit>>(StringComparer.Ordinal);
        foreach (var limit in _engine.EvaluateAll(context))
        {
            if (string.IsNullOrEmpty(limit.Name)) continue;

            if (!byVariable.TryGetValue(limit.Name, out var kinds))
            {
                kinds = new Dictionary<VariableLimitKind, VariableLimit>();
                byVariable[limit.Name] = kinds;
            }

            kinds.TryAdd(limit.Kind, limit);
        }

        return byVariable.Values
            .Select(Combine)
            .OrderBy(l => l.Scope)
            .ThenBy(l => l.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Folds everything known about one variable into a single statement. The type's own range is
    /// dropped as soon as anything narrower is known, since it would only dilute a real finding.
    /// </summary>
    private static VariableLimit Combine(Dictionary<VariableLimitKind, VariableLimit> kinds)
    {
        var parts = kinds.Values.ToList();
        if (parts.Count > 1)
        {
            parts.RemoveAll(p => p.Kind == VariableLimitKind.DeclaredType);
        }

        // Presence first ("must not be null, 1 to 10"): a value that may be absent has to be
        // dealt with before any question of its range arises.
        parts = parts
            .OrderBy(p => p.Kind switch
            {
                VariableLimitKind.Presence => 0,
                VariableLimitKind.Membership => 1,
                VariableLimitKind.Range => 2,
                VariableLimitKind.Size => 3,
                _ => 4,
            })
            .ToList();

        var strongest = parts.OrderBy(p => p.Confidence).First();
        return new VariableLimit
        {
            Name = strongest.Name,
            Type = strongest.Type,
            Scope = strongest.Scope,
            Kind = parts[0].Kind,
            Limit = string.Join(", ", parts.Select(p => p.Limit)),
            Evidence = string.Join("; ", parts.Select(p => p.Evidence).Distinct(StringComparer.Ordinal)),
            Source = strongest.Source,
            Confidence = strongest.Confidence,
        };
    }

    // ── Rules: the code restricts the value ──────────────────────────────────

    /// <summary>
    /// A guard rejecting values outside a range: <c>if (level &lt; 0 || level &gt; 100) throw</c>.
    /// </summary>
    private static IEnumerable<VariableLimit> RuleRangeGuard(MethodAnalysisContext context)
    {
        var known = BuildDeclaredVariables(context);

        foreach (var line in RejectionLines(context))
        {
            foreach (Match match in SafeRegex.Matches(
                line,
                @"\b([A-Za-z_]\w*)\s*<(=?)\s*" + ValuePattern + @"\s*\|\|\s*\1\s*>(=?)\s*" + ValuePattern))
            {
                var name = match.Groups[1].Value;
                if (!known.TryGetValue(name, out var declared)) continue;
                if (!TryResolve(match.Groups[3].Value, context, out var lowValue)) continue;
                if (!TryResolve(match.Groups[5].Value, context, out var highValue)) continue;

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

        foreach (var line in RejectionLines(context))
        {
            foreach (Match match in SafeRegex.Matches(
                line, @"\b([A-Za-z_]\w*)\s*(>=|<=|>|<)\s*" + ValuePattern + @"\b"))
            {
                var name = match.Groups[1].Value;
                if (!known.TryGetValue(name, out var declared)) continue;
                if (IsLengthExpression(line, name)) continue;
                if (!TryResolve(match.Groups[3].Value, context, out var value)) continue;

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

        foreach (var line in RejectionLines(context))
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
                    VariableLimitSource.Guard, AnalysisConfidence.High, VariableLimitKind.Size);
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

        // ThrowIfNull is a statement in its own right rather than part of an "if", so it is read
        // from the whole body; the rest are conditions and are read from the guard lines.
        foreach (Match match in SafeRegex.Matches(
            CodeOnly(context), @"ThrowIfNull\s*\(\s*([A-Za-z_]\w*)"))
        {
            var name = match.Groups[1].Value;
            if (!known.TryGetValue(name, out var declared)) continue;

            yield return Build(name, declared, "must not be null", match.Value,
                VariableLimitSource.Guard, AnalysisConfidence.High, VariableLimitKind.Presence);
        }

        foreach (var line in RejectionLines(context))
        {
            // "x == null" and "x is null" say the same thing; C# has largely moved to the second,
            // so a rule that knows only the first misses most modern code.
            foreach (Match match in SafeRegex.Matches(
                line,
                @"\b([A-Za-z_]\w*)\s*(?:==\s*(?:null|nullptr)|is\s+null)\b"
                    + @"|(?:IsNullOrEmpty|IsNullOrWhiteSpace)\s*\(\s*([A-Za-z_]\w*)\s*\)"))
            {
                var name = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
                if (string.IsNullOrEmpty(name) || !known.TryGetValue(name, out var declared)) continue;

                var text = match.Groups[2].Success && IsTextType(declared.Type)
                    ? "must not be empty"
                    : "must not be null";

                yield return Build(name, declared, text, match.Value,
                    VariableLimitSource.Guard, AnalysisConfidence.High, VariableLimitKind.Presence);
            }
        }
    }

    /// <summary>
    /// A value the method divides by. Nothing in the source has to say so for this to be a real
    /// limit: dividing by zero is a fault whether or not anyone guarded against it, so the
    /// division itself is the evidence.
    /// </summary>
    private static IEnumerable<VariableLimit> RuleDivisor(MethodAnalysisContext context)
    {
        var known = BuildDeclaredVariables(context);

        foreach (var (name, declared) in known)
        {
            // The name must be the whole divisor. In "total / ir.Classes.Count" what must not be
            // zero is the count, not ir — so anything followed by a member access, an index or a
            // call is a different expression that merely starts with this name.
            if (!SafeRegex.IsMatch(
                CodeOnly(context),
                $@"[/%]\s*{Regex.Escape(name)}\b(?!\s*[.\[(])")) continue;

            yield return Build(name, declared, "must not be zero",
                $"used as a divisor: / {name}",
                VariableLimitSource.Guard, AnalysisConfidence.Medium, VariableLimitKind.Range);
        }
    }

    /// <summary>
    /// A guard whose bounds are themselves variables: <c>if (value &lt; min || value &gt; max)
    /// throw</c>. There is no number to show, but "between min and max" tells the reader where to
    /// look, which is more use than falling back to the range of the type.
    /// </summary>
    private static IEnumerable<VariableLimit> RuleSymbolicRangeGuard(MethodAnalysisContext context)
    {
        var known = BuildDeclaredVariables(context);
        var lower = new Dictionary<string, (string Bound, string Evidence)>(StringComparer.Ordinal);
        var upper = new Dictionary<string, (string Bound, string Evidence)>(StringComparer.Ordinal);

        // Accumulated across guard lines rather than matched on one, because the two ends are
        // often written as separate statements — "if (v < low) return low;" on one line and
        // "if (v > high) return high;" on the next is the same restriction as the pair joined
        // by "||", and a one-line pattern would see neither.
        foreach (var line in RejectionLines(context))
        {
            foreach (Match match in SafeRegex.Matches(
                line, @"\b([A-Za-z_]\w*)\s*(<=|>=|<|>)\s*([A-Za-z_]\w*)\b"))
            {
                var name = match.Groups[1].Value;
                var other = match.Groups[3].Value;

                // Both ends must be variables the method actually has, or this is a misread of
                // some unrelated pair of words.
                if (!known.ContainsKey(name) || !known.ContainsKey(other)) continue;
                if (string.Equals(name, other, StringComparison.Ordinal)) continue;

                // The guard names what it refuses, so refusing "below low" permits "low upwards".
                var evidence = Condense(match.Value);
                if (match.Groups[2].Value[0] == '<')
                {
                    lower.TryAdd(name, (other, evidence));
                }
                else
                {
                    upper.TryAdd(name, (other, evidence));
                }
            }
        }

        foreach (var (name, low) in lower)
        {
            if (!upper.TryGetValue(name, out var high)) continue;
            if (string.Equals(low.Bound, high.Bound, StringComparison.Ordinal)) continue;

            yield return Build(name, known[name], $"between {low.Bound} and {high.Bound}",
                $"{low.Evidence}; {high.Evidence}",
                VariableLimitSource.Guard, AnalysisConfidence.High);
        }
    }

    /// <summary>
    /// A guard admitting only a handful of values: <c>if (mode != 1 &amp;&amp; mode != 2) throw</c>
    /// permits "1 or 2". A short list of permitted values is a stronger statement than any range
    /// covering them, so it is reported as its own kind.
    /// </summary>
    private static IEnumerable<VariableLimit> RuleAllowedValues(MethodAnalysisContext context)
    {
        var known = BuildDeclaredVariables(context);

        foreach (var line in RejectionLines(context))
        {
            foreach (Match match in SafeRegex.Matches(
                line,
                @"\b([A-Za-z_]\w*)\s*!=\s*" + NumberPattern
                    + @"(?:\s*&&\s*\1\s*!=\s*" + NumberPattern + @")+"))
            {
                var name = match.Groups[1].Value;
                if (!known.TryGetValue(name, out var declared)) continue;

                // The rejected values are every literal compared against the name on this match.
                var values = new List<string>();
                foreach (Match part in SafeRegex.Matches(
                    match.Value, $@"{Regex.Escape(name)}\s*!=\s*" + NumberPattern))
                {
                    if (TryParse(part.Groups[1].Value, out var value))
                    {
                        var text = Number(value);
                        if (!values.Contains(text, StringComparer.Ordinal)) values.Add(text);
                    }
                }

                if (values.Count < 2) continue;

                yield return Build(name, declared, JoinValues(values), match.Value,
                    VariableLimitSource.Guard, AnalysisConfidence.High, VariableLimitKind.Membership);
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
            CodeOnly(context),
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
            CodeOnly(context), @"\b([A-Za-z_]\w*)\s*>=\s*'(.)'\s*&&\s*\1\s*<=\s*'(.)'"))
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
            CodeOnly(context), @"\b([A-Za-z_]\w*)\s*(>=|<=|>|<)\s*" + NumberPattern + @"\b"))
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

        // Both ends, or nothing. A lone comparison is almost always a branch rather than a
        // restriction: "if (angle > 0)" on the result of IndexOf does not mean angle is positive,
        // it means the code does something different when the character was found — angle is
        // routinely -1. Reported as a limit that would be a plain falsehood. Two ends bracketing
        // a value are far more often a range the method genuinely works within, and a value the
        // method truly refuses is caught by the guard rules, which read a throw rather than a
        // branch.
        foreach (var (name, low) in lower)
        {
            if (!upper.TryGetValue(name, out var high)) continue;
            if (low.Bound.Value > high.Bound.Value) continue;

            yield return Build(
                name, known[name], DescribeRange(low.Bound, high.Bound),
                $"{low.Evidence}, {high.Evidence}",
                VariableLimitSource.Comparison, AnalysisConfidence.Medium);
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
            CodeOnly(context),
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
                VariableLimitSource.DeclaredType, AnalysisConfidence.Low,
                VariableLimitKind.DeclaredType);
        }
    }

    // ── Reading the source ───────────────────────────────────────────────────

    /// <summary>
    /// The method body with comments and string literals blanked out.
    /// </summary>
    /// <remarks>
    /// A limit read from a commented-out line is worse than no limit at all: it states a
    /// restriction the running code does not apply, and the reader has no way to know. The same
    /// goes for text inside a string. Both are replaced by spaces rather than removed, so every
    /// remaining character keeps its position and quoted evidence still lines up with the source.
    /// Cached per context because a dozen rules each need it.
    /// </remarks>
    private static string CodeOnly(MethodAnalysisContext context) =>
        CodeOnlyCache.GetValue(context, static ctx => SafeRegex.Replace(
            ctx.SourceBody,
            @"//[^\n]*|/\*.*?\*/|""(?:\\.|[^""\\\n])*""|'(?:\\.|[^'\\\n])*'",
            static match => match.Value[0] == '\''
                // A character literal is meaningful to the character-range rule, so it stays.
                ? match.Value
                : new string(' ', match.Value.Length)));

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<MethodAnalysisContext, string>
        CodeOnlyCache = new();

    /// <summary>
    /// The lines that refuse a value: a test paired with a <c>throw</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only a throw counts. An early <c>return</c> looks similar but means something different:
    /// <c>if (value &lt; low) return low;</c> does not forbid a low value, it substitutes one, so
    /// the caller may pass anything. Treating that as a refusal would state a restriction the
    /// method does not impose — and <c>if (x is null) return;</c> would be read as "must not be
    /// null" when it means the exact opposite, that null is handled.
    /// </para>
    /// <para>
    /// Matched on word boundaries. Plain substring matching found "return" inside the identifier
    /// <c>returnStatements</c>, which turned an ordinary branch into a refusal and produced the
    /// nonsense limit "at most 0 items".
    /// </para>
    /// <para>
    /// A guard written across two lines counts as well. Brace style puts the throw on its own
    /// line, which is how most C++ and a good deal of C# is written, and a rule that insisted on
    /// one physical line would miss all of it. The condition is joined to the throw only when the
    /// throw is the first statement of the block it opens, so one guard's condition can never
    /// pair with a later, unrelated throw.
    /// </para>
    /// <para>
    /// Splitting into lines first keeps one line's condition from pairing with another's bound,
    /// which whole-body matching would allow.
    /// </para>
    /// </remarks>
    private static IEnumerable<string> RejectionLines(MethodAnalysisContext context)
    {
        var lines = CodeOnly(context).Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (!SafeRegex.IsMatch(line, @"\bif\b")) continue;

            if (SafeRegex.IsMatch(line, @"\bthrow\b"))
            {
                yield return line;
                continue;
            }

            // The condition opened a block. Only the first statement inside it can be the
            // refusal; anything else means this is an ordinary branch.
            if (!line.EndsWith('{')) continue;

            var next = NextStatement(lines, i + 1);
            if (next is not null && SafeRegex.IsMatch(next, @"^\s*throw\b"))
            {
                yield return line + " " + next.Trim();
            }
        }
    }

    /// <summary>The next line carrying code, or null if the block ends first.</summary>
    private static string? NextStatement(string[] lines, int start)
    {
        for (var i = start; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) continue;
            return line;
        }

        return null;
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
            // A field named only in a comment is not one this method uses.
            if (!CodeOnly(context).Contains(field.Name, StringComparison.Ordinal)) continue;
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

    /// <summary>Renders a short list as "1, 2 or 3" rather than a bare comma-separated run.</summary>
    private static string JoinValues(List<string> values) =>
        values.Count == 2
            ? $"{values[0]} or {values[1]}"
            : $"{string.Join(", ", values.Take(values.Count - 1))} or {values[^1]}";

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

    /// <summary>
    /// A bound as written: either a literal, or the name of something standing in for one.
    /// </summary>
    private const string ValuePattern = @"([A-Za-z_]\w*|-?\d+(?:\.\d+)?[fFdDmMlLuU]{0,2})";

    /// <summary>
    /// Turns a bound into a number, following a name to its value where the name stands for a
    /// fixed one — so <c>if (count &gt; MaxItems)</c> reports "100 or less" rather than falling
    /// back to the range of the type.
    /// </summary>
    private static bool TryResolve(string token, MethodAnalysisContext context, out decimal value)
    {
        // The token now arrives whole, so any type suffix C# or C++ attached — the "m" of 0m —
        // comes with it and has to come off before the number will parse.
        var literal = token.TrimEnd('f', 'F', 'd', 'D', 'm', 'M', 'l', 'L', 'u', 'U');
        if (literal.Length > 0 && char.IsAsciiDigit(literal[^1]) && TryParse(literal, out value))
        {
            return true;
        }

        return FixedValues(context).TryGetValue(token, out value);
    }

    /// <summary>
    /// Names that stand for a fixed number: fields and locals initialised to a literal and never
    /// assigned again anywhere in this method.
    /// </summary>
    /// <remarks>
    /// The reassignment check is what makes this safe. <c>int limit = 5; limit = Compute();</c>
    /// initialises to a literal but does not stand for it, and quoting 5 would state a bound the
    /// method never applies. A name that is written to is not a constant, so it is not followed.
    /// The condition itself still appears in the evidence column, so a reader can always see
    /// which name produced the number.
    /// </remarks>
    private static Dictionary<string, decimal> FixedValues(MethodAnalysisContext context) =>
        FixedValueCache.GetValue(context, static ctx =>
        {
            var map = new Dictionary<string, decimal>(StringComparer.Ordinal);
            var body = CodeOnly(ctx);

            var candidates = (ctx.Method.ParentClass?.Fields ?? []).Concat(ctx.Method.LocalVariables);
            foreach (var candidate in candidates)
            {
                if (string.IsNullOrEmpty(candidate.Name) || candidate.InitialValue is null) continue;
                if (!TryParse(candidate.InitialValue.Trim(), out var literal)) continue;

                // "=" but not "==", and not the "=" of the declaration this value came from.
                var assignments = SafeRegex.Matches(
                    body, $@"\b{Regex.Escape(candidate.Name)}\s*(?:[-+*/]?=)(?!=)").Count;
                var declared = SafeRegex.IsMatch(
                    body, $@"\b{Regex.Escape(candidate.Name)}\s*=\s*{Regex.Escape(candidate.InitialValue.Trim())}");

                if (assignments > (declared ? 1 : 0)) continue;

                map[candidate.Name] = literal;
            }

            return map;
        });

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<MethodAnalysisContext, Dictionary<string, decimal>>
        FixedValueCache = new();

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
        AnalysisConfidence confidence,
        VariableLimitKind kind = VariableLimitKind.Range) =>
        new()
        {
            Name = name,
            Type = declared.Type,
            Scope = declared.Scope,
            Kind = kind,
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

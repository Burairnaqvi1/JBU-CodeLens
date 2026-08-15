namespace JBU.CodeLens.Shared.Utilities;

/// <summary>
/// The model's pre/post-condition output, split into its two labeled groups.
/// <paramref name="Ungrouped"/> carries bullets that appeared before any marker, non-empty only
/// when the model ignored the requested format.
/// </summary>
public readonly record struct PrePostConditionGroups(
    IReadOnlyList<string> Preconditions,
    IReadOnlyList<string> Postconditions,
    IReadOnlyList<string> Ungrouped)
{
    /// <summary>
    /// True when at least one marker was found, so the bullets can be presented under their own
    /// "preconditions" / "postconditions" labels. False means callers should fall back to
    /// rendering a single undifferentiated list.
    /// </summary>
    public bool IsGrouped => Preconditions.Count > 0 || Postconditions.Count > 0;
}

/// <summary>
/// Parses the marker format that <c>ExplanationService.GeneratePrePostConditions</c> asks the
/// model to produce: a <c>PRE:</c> line, its bullets, then a <c>POST:</c> line and its bullets.
/// <para>
/// A 1.5B local model does not honor an output format every time, so the parser is deliberately
/// lenient (it accepts <c>Preconditions:</c>, <c>- POST:</c>, and similar) and reports failure
/// through <see cref="PrePostConditionGroups.IsGrouped"/> rather than throwing or guessing, 
/// mislabeling a postcondition as a precondition is worse than showing one flat list.
/// </para>
/// </summary>
public static class PrePostConditionText
{
    /// <summary>Marker the prompt asks the model to put before the precondition bullets.</summary>
    public const string PreMarker = "PRE:";

    /// <summary>Marker the prompt asks the model to put before the postcondition bullets.</summary>
    public const string PostMarker = "POST:";

    public static PrePostConditionGroups Split(string? text)
    {
        var pre = new List<string>();
        var post = new List<string>();
        var ungrouped = new List<string>();

        // A bracketed string is an error/unavailable message, not model output to be parsed.
        if (string.IsNullOrWhiteSpace(text) || text.TrimStart().StartsWith('['))
        {
            return new PrePostConditionGroups(pre, post, ungrouped);
        }

        var current = ungrouped;
        foreach (var rawLine in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var line = rawLine.TrimStart('-', '•', '*', '#', ' ').Trim();
            if (line.Length == 0) continue;

            var marker = MatchMarker(line);
            if (marker is not null)
            {
                current = marker == PreMarker ? pre : post;

                // "POST: the method returns true", the model put content on the marker line
                // itself instead of the line below, so keep the remainder as the first bullet.
                var inline = line[(line.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim();
                if (inline.Length > 0) current.Add(inline);
                continue;
            }

            current.Add(line);
        }

        return new PrePostConditionGroups(pre, post, ungrouped);
    }

    /// <summary>
    /// Returns the marker a heading line denotes, or <c>null</c> when the line is a normal bullet.
    /// Checked before the colon so "Postconditions:" and "POST:" both match.
    /// </summary>
    private static string? MatchMarker(string line)
    {
        var colon = line.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0) return null;

        var head = line[..colon].Trim();

        // "Preconditions" / "Post-conditions". ctrip separators so one comparison covers each
        // spelling the model reaches for.
        head = head.Replace("-", string.Empty, StringComparison.Ordinal)
                   .Replace(" ", string.Empty, StringComparison.Ordinal);

        if (head.Equals("POST", StringComparison.OrdinalIgnoreCase) ||
            head.Equals("POSTCONDITION", StringComparison.OrdinalIgnoreCase) ||
            head.Equals("POSTCONDITIONS", StringComparison.OrdinalIgnoreCase))
        {
            return PostMarker;
        }

        if (head.Equals("PRE", StringComparison.OrdinalIgnoreCase) ||
            head.Equals("PRECONDITION", StringComparison.OrdinalIgnoreCase) ||
            head.Equals("PRECONDITIONS", StringComparison.OrdinalIgnoreCase))
        {
            return PreMarker;
        }

        return null;
    }
}

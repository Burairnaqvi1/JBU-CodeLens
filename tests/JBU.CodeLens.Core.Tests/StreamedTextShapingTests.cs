using JBU.CodeLens.Core.AI;

namespace JBU.CodeLens.Core.Tests;

/// <summary>
/// Covers the shaping applied to text streamed from the language model while it generates.
/// </summary>
/// <remarks>
/// The brief description used to stream the model's raw output and then return a trimmed version
/// of it. On screen that read as the text growing to three or four lines and collapsing to one the
/// instant generation finished. The fix is that the streamed preview is shaped by the same function
/// as the returned value, so it converges rather than overshooting; these tests pin that property.
/// </remarks>
public class StreamedTextShapingTests
{
    /// <summary>A model answer that runs well past the one-sentence limit a brief description keeps.</summary>
    private const string LongAnswer =
        "Validates the supplied order before processing it. It then walks every line item and " +
        "accumulates the running total, applying the discount rules where they match. Finally it " +
        "writes the result back to the repository and returns the computed total to the caller.";

    /// <summary>Rebuilds the cumulative snapshots the model callback produces, word by word.</summary>
    private static IEnumerable<string> CumulativeStream(string full)
    {
        var words = full.Split(' ');
        for (var count = 1; count <= words.Length; count++)
        {
            yield return string.Join(' ', words.Take(count));
        }
    }

    [Fact]
    public void ShapedStream_EndsOnExactlyTheValueThatWillBeReturned()
    {
        var final = ExplanationService.TruncateProse(LongAnswer, maxSentences: 1, maxWords: 35);

        var lastPreview = CumulativeStream(LongAnswer)
            .Select(partial => ExplanationService.TruncateProse(partial, maxSentences: 1, maxWords: 35))
            .Last();

        // If these differ, the display changes at the moment generation ends — which is exactly the
        // shrink this shaping exists to prevent.
        Assert.Equal(final, lastPreview);
    }

    [Fact]
    public void ShapedStream_NeverShrinks()
    {
        var previews = CumulativeStream(LongAnswer)
            .Select(partial => ExplanationService.TruncateProse(partial, maxSentences: 1, maxWords: 35))
            .ToList();

        for (var i = 1; i < previews.Count; i++)
        {
            Assert.True(
                previews[i].Length >= previews[i - 1].Length,
                $"Preview shrank from {previews[i - 1].Length} to {previews[i].Length} characters " +
                $"at update {i}: \"{previews[i - 1]}\" then \"{previews[i]}\".");
        }
    }

    [Fact]
    public void UnshapedStream_WouldOvershootTheFinalValue()
    {
        // Guards the premise: without shaping, the raw stream really does grow past the returned
        // value, so this is a real defect rather than shaping added for its own sake.
        var final = ExplanationService.TruncateProse(LongAnswer, maxSentences: 1, maxWords: 35);
        var rawLast = CumulativeStream(LongAnswer).Last();

        Assert.True(
            rawLast.Length > final.Length,
            "The sample answer must exceed the one-sentence limit for this test to mean anything.");
    }

    [Fact]
    public void ShapedStream_StopsGrowingOnceTheFirstSentenceIsComplete()
    {
        var previews = CumulativeStream(LongAnswer)
            .Select(partial => ExplanationService.TruncateProse(partial, maxSentences: 1, maxWords: 35))
            .ToList();

        var settled = previews.FindIndex(p => p.EndsWith('.'));
        Assert.True(settled >= 0, "The first sentence should complete partway through the stream.");

        // Everything after the first sentence closes must be identical: the user sees the line
        // settle and stay, rather than continuing to grow and then snapping back.
        Assert.All(previews.Skip(settled), p => Assert.Equal(previews[settled], p));
    }

    /// <summary>
    /// Every shaping function a streaming entry point uses, with a sample long enough to overshoot
    /// it. Adding a new streamed section means adding its shaper here.
    /// </summary>
    public static TheoryData<string, Func<string, string>> Shapers => new()
    {
        { "brief description", ExplanationService.ShapeBriefDescription },
        { "method explanation", ExplanationService.ShapeExplanation },
        { "class summary", ExplanationService.ShapeClassSummary },
    };

    [Theory]
    [MemberData(nameof(Shapers))]
    public void EveryShaper_ConvergesRatherThanOvershooting(string name, Func<string, string> shape)
    {
        ArgumentNullException.ThrowIfNull(shape);

        var final = shape(LongAnswer);
        var previews = CumulativeStream(LongAnswer).Select(shape).ToList();

        Assert.Equal(final, previews[^1]);
        Assert.All(previews, p => Assert.True(
            p.Length <= final.Length,
            $"{name}: a preview reached {p.Length} characters against a final {final.Length}, " +
            "so the text would visibly shrink when generation ends."));
    }

    [Fact]
    public void ClassSummaryShaper_RemovesTrailingMarkupFragments()
    {
        // Small models tack these on after the final sentence; the shaper strips them, and the
        // streamed preview must strip them too rather than flashing them on screen.
        Assert.Equal(
            "Coordinates order processing.",
            ExplanationService.ShapeClassSummary("Coordinates order processing. <|"));
    }

    [Fact]
    public void BracketedMessages_AreLeftAlone()
    {
        // Errors and unavailable notices are returned in brackets and must not be trimmed into
        // something misleading.
        const string error = "[Inference failed: the model ran out of context. Try a shorter method.]";

        Assert.Equal(error, ExplanationService.TruncateProse(error, maxSentences: 1, maxWords: 35));
    }
}

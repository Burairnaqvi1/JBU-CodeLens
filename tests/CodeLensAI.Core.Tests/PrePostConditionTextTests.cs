using CodeLensAI.Shared.Utilities;

namespace CodeLensAI.Core.Tests;

/// <summary>
/// The model is a 1.5B local LLM that honors an output format most but not all of the time, so
/// these cases are drawn from shapes it actually produced (see tests/TimingAnalysis/results.json)
/// rather than only the format the prompt requests.
/// </summary>
public class PrePostConditionTextTests
{
    [Fact]
    public void Split_MarkersOnOwnLines_GroupsBullets()
    {
        var groups = PrePostConditionText.Split(
            "PRE:\n- `order` must not be null.\n- `order.Id` must be greater than 0.\nPOST:\n- Returns the total.");

        Assert.True(groups.IsGrouped);
        Assert.Equal(new[] { "`order` must not be null.", "`order.Id` must be greater than 0." }, groups.Preconditions);
        Assert.Equal(new[] { "Returns the total." }, groups.Postconditions);
        Assert.Empty(groups.Ungrouped);
    }

    [Fact]
    public void Split_NoMarkers_ReportsNotGroupedAndKeepsEveryBullet()
    {
        // Observed output: the model skipped the format entirely and emitted a flat list. The UI
        // relies on IsGrouped being false here to fall back to one undifferentiated block.
        var groups = PrePostConditionText.Split(
            "- `order` must not be null.\n- `order.Items` must not be empty.");

        Assert.False(groups.IsGrouped);
        Assert.Equal(2, groups.Ungrouped.Count);
        Assert.Empty(groups.Preconditions);
        Assert.Empty(groups.Postconditions);
    }

    [Fact]
    public void Split_InlineContentAfterMarker_KeepsItAsFirstBullet()
    {
        // Observed output: "- Preconditions: `apiEndpoint` must not be null or empty."
        var groups = PrePostConditionText.Split(
            "- Preconditions: `apiEndpoint` must not be null or empty.\n" +
            "- Postconditions: Returns true when the refresh succeeded.");

        Assert.True(groups.IsGrouped);
        Assert.Equal(new[] { "`apiEndpoint` must not be null or empty." }, groups.Preconditions);
        Assert.Equal(new[] { "Returns true when the refresh succeeded." }, groups.Postconditions);
    }

    [Theory]
    [InlineData("Preconditions:")]
    [InlineData("PRE:")]
    [InlineData("- Pre-conditions:")]
    [InlineData("### Precondition:")]
    public void Split_AcceptsMarkerSpellingVariants(string marker)
    {
        var groups = PrePostConditionText.Split($"{marker}\n- guard clause enforced.");

        Assert.True(groups.IsGrouped);
        Assert.Equal(new[] { "guard clause enforced." }, groups.Preconditions);
    }

    [Fact]
    public void Split_BulletContainingColon_IsNotMistakenForAMarker()
    {
        var groups = PrePostConditionText.Split("PRE:\n- Throws ArgumentNullException: order was null.");

        Assert.Equal(new[] { "Throws ArgumentNullException: order was null." }, groups.Preconditions);
        Assert.Empty(groups.Postconditions);
    }

    [Fact]
    public void Split_EmptyPostSection_StaysGroupedWithNoPostconditions()
    {
        var groups = PrePostConditionText.Split("PRE:\n- `order` must not be null.\nPOST:");

        Assert.True(groups.IsGrouped);
        Assert.Single(groups.Preconditions);
        Assert.Empty(groups.Postconditions);
    }

    [Theory]
    [InlineData("[The explanation model is not available.]")]
    [InlineData("")]
    [InlineData("   ")]
    public void Split_ErrorOrEmptyText_YieldsNothingToRender(string text)
    {
        var groups = PrePostConditionText.Split(text);

        Assert.False(groups.IsGrouped);
        Assert.Empty(groups.Ungrouped);
    }

    [Fact]
    public void Split_Null_YieldsNothingToRender()
    {
        var groups = PrePostConditionText.Split(null);

        Assert.False(groups.IsGrouped);
        Assert.Empty(groups.Ungrouped);
    }
}

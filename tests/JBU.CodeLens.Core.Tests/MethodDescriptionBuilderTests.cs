using JBU.CodeLens.Core.Analysis;
using JBU.CodeLens.Shared.Models;

namespace JBU.CodeLens.Core.Tests;

/// <summary>
/// Tests for the deterministic fallback description sentence. These strings are shown verbatim
/// under "Brief Description" whenever a method has no XML docs and no AI output, so grammatical
/// regressions are user-visible immediately.
/// </summary>
public class MethodDescriptionBuilderTests
{
    private static MethodInfo Method(
        string name,
        string returnType = "void",
        string[]? parameters = null,
        string? parentClass = null)
    {
        var method = new MethodInfo
        {
            Name = name,
            ReturnType = returnType,
            Parameters = (parameters ?? Array.Empty<string>()).ToList(),
        };

        if (parentClass is not null)
        {
            method.ParentClass = new ClassInfo { Name = parentClass };
        }

        return method;
    }

    [Fact]
    public void Constructor_WithParameters_DescribesConstruction()
    {
        var text = MethodDescriptionBuilder.Build(
            Method("Widget", parameters: new[] { "string path" }, parentClass: "Widget"));

        Assert.Equal("Constructs a new Widget instance using path.", text);
    }

    [Fact]
    public void BareVerb_TreatsParameterAsObject()
    {
        var text = MethodDescriptionBuilder.Build(
            Method("Apply", parameters: new[] { "Theme theme" }));

        Assert.Equal("Applies the given theme.", text);
    }

    [Fact]
    public void VerbWithObject_KeepsParametersAsInstruments()
    {
        var text = MethodDescriptionBuilder.Build(
            Method("SaveFile", parameters: new[] { "string path" }));

        Assert.Equal("Saves file using path.", text);
    }

    [Fact]
    public void BooleanPrefix_ReadsAsCondition()
    {
        var text = MethodDescriptionBuilder.Build(Method("IsValid", returnType: "bool"));

        Assert.Equal("Determines whether the valid condition holds.", text);
    }

    [Fact]
    public void BoolReturn_WithOnlyParameters_DoesNotRepeatTheParameterName()
    {
        var text = MethodDescriptionBuilder.Build(
            Method("Validate", returnType: "bool", parameters: new[] { "string input" }));

        // Regression guard: the subject and the "based on" clause must not both be the
        // parameter name ("Determines whether input, based on input.").
        //
        // The expected sentence changed when bool-returning methods named with a plain verb were
        // moved onto the action path. "Determines whether input." was grammatical only by accident
        // — it states a condition and then names a parameter instead of the condition. The guard
        // this test exists for is unchanged and asserted directly below.
        Assert.Equal("Validates the given input, returning true when the condition holds, otherwise false.", text);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, @"\binput\b"));
        Assert.DoesNotContain("based on", text, StringComparison.Ordinal);
    }

    [Fact]
    public void IntReturn_MentionsComputedValue()
    {
        var text = MethodDescriptionBuilder.Build(Method("GetTotal", returnType: "int"));

        Assert.Equal("Gets total, returning the computed int value.", text);
    }

    [Fact]
    public void PlainTask_IsTreatedAsVoid()
    {
        var text = MethodDescriptionBuilder.Build(
            Method("LoadData", returnType: "Task", parameters: new[] { "string path" }));

        Assert.Equal("Loads data using path.", text);
    }

    [Fact]
    public void GenericTask_MentionsAsynchronousCompletion()
    {
        var text = MethodDescriptionBuilder.Build(
            Method("FetchItems", returnType: "Task<List<int>>"));

        Assert.Equal("Fetches items, returning a task that completes asynchronously.", text);
    }

    [Fact]
    public void SnakeCaseName_IsSplitLikeCamelCase()
    {
        var text = MethodDescriptionBuilder.Build(
            Method("save_file", parameters: new[] { "string path" }));

        Assert.Equal("Saves file using path.", text);
    }

    [Fact]
    public void CppReferenceParameter_UsesBareName()
    {
        var text = MethodDescriptionBuilder.Build(
            Method("SetName", parameters: new[] { "const std::string& name" }));

        Assert.Equal("Sets name using name.", text);
    }

    [Fact]
    public void ThreeParameters_UseOxfordComma()
    {
        var text = MethodDescriptionBuilder.Build(
            Method("DrawLine", parameters: new[] { "int x", "int y", "Color color" }));

        Assert.Equal("Draws line using x, y, and color.", text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("_")]
    [InlineData("X")]
    [InlineData("平均値を計算")]
    public void NeverThrows_AndAlwaysReturnsASentence(string name)
    {
        var text = MethodDescriptionBuilder.Build(Method(name));

        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.EndsWith(".", text, StringComparison.Ordinal);
    }
}

using CodeLensAI.Core.Analysis;
using CodeLensAI.Shared.Models;

namespace CodeLensAI.Core.Tests;

/// <summary>
/// Tests for the deterministic class description. This is the immediate "What This Class Does"
/// text for every class without XML docs, so it must be grammatical and built only from
/// verified facts.
/// </summary>
public class ClassDescriptionBuilderTests
{
    private static ClassInfo Class(
        string name,
        int methods = 0,
        int properties = 0,
        CodeCategory category = CodeCategory.BusinessLogic)
    {
        var classInfo = new ClassInfo { Name = name, Category = category };
        for (var i = 0; i < methods; i++)
        {
            classInfo.Methods.Add(new MethodInfo { Name = $"Method{i + 1}" });
        }

        for (var i = 0; i < properties; i++)
        {
            classInfo.Properties.Add(new PropertyInfo { Name = $"Property{i + 1}" });
        }

        return classInfo;
    }

    [Fact]
    public void Class_WithMethodsAndDependencies_ListsBoth()
    {
        var classInfo = Class("OrderProcessingSystem", methods: 4);
        classInfo.Methods[0].Name = "ValidateOrder";
        classInfo.Methods[1].Name = "CalculateOrderTotal";
        classInfo.Methods[2].Name = "GetTopSpenders";
        classInfo.Methods[3].Name = "FormatInvoice";
        classInfo.Dependencies.AddRange(new[] { "Product", "Order" });

        Assert.Equal(
            "Business-logic class with 4 methods, including ValidateOrder, CalculateOrderTotal, " +
            "GetTopSpenders, and 1 more; depends on Product and Order.",
            ClassDescriptionBuilder.Build(classInfo));
    }

    [Fact]
    public void Interface_UsesDefiningWording()
    {
        var classInfo = Class("IExporter", methods: 1, properties: 1);
        classInfo.Methods[0].Name = "Export";

        Assert.Equal(
            "Business-logic interface defining 1 method and 1 property, including Export.",
            ClassDescriptionBuilder.Build(classInfo));
    }

    [Fact]
    public void GuiClass_WithBaseClass_MentionsExtends()
    {
        var classInfo = Class("MainWindow", methods: 2, category: CodeCategory.GuiLogic);
        classInfo.BaseClassName = "Window";

        Assert.StartsWith("User-interface class with 2 methods", ClassDescriptionBuilder.Build(classInfo));
        Assert.Contains("; extends Window.", ClassDescriptionBuilder.Build(classInfo));
    }

    [Fact]
    public void EmptyClass_SaysSoInsteadOfBreaking()
    {
        Assert.Equal(
            "Business-logic class with no members declared directly in it.",
            ClassDescriptionBuilder.Build(Class("Marker")));
    }

    [Fact]
    public void ManyDependencies_AreCappedWithARemainder()
    {
        var classInfo = Class("Hub", methods: 1);
        classInfo.Dependencies.AddRange(new[] { "A", "B", "C", "D", "E", "F" });

        Assert.EndsWith("depends on A, B, C, D, and 2 more.", ClassDescriptionBuilder.Build(classInfo));
    }

    [Fact]
    public void NeverThrows_OnDegenerateInput()
    {
        var text = ClassDescriptionBuilder.Build(new ClassInfo());

        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.EndsWith(".", text);
    }
}

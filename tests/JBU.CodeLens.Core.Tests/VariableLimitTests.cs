using JBU.CodeLens.Core.Analysis;
using JBU.CodeLens.Shared.Models;

namespace JBU.CodeLens.Core.Tests;

/// <summary>
/// Covers the inference of the value range each variable is allowed to hold inside a method.
/// </summary>
/// <remarks>
/// Assertions target the range and the rule that produced it rather than the exact wording, so
/// rephrasing a limit does not break a test, but reading the wrong range from the code does.
/// </remarks>
public class VariableLimitTests
{
    private static MethodAnalysisContext Context(
        string sourceBody,
        string[]? parameters = null,
        (string Name, string Type)[]? locals = null,
        string fileName = "Sample.cs")
    {
        var parentClass = new ClassInfo
        {
            Name = "Sample",
            SourceFilePath = @"C:\proj\" + fileName,
        };

        var method = new MethodInfo
        {
            Name = "Operate",
            ReturnType = "void",
            AccessModifier = "public",
            ParentClass = parentClass,
        };

        foreach (var p in parameters ?? [])
        {
            method.Parameters.Add(p);
        }

        foreach (var (name, type) in locals ?? [])
        {
            method.LocalVariables.Add(new VariableInfo { Name = name, Type = type });
        }

        method.XmlDocTags["sourceCode"] = sourceBody;
        parentClass.Methods.Add(method);
        return new MethodAnalysisContext(method);
    }

    private static IReadOnlyList<VariableLimit> Analyze(MethodAnalysisContext context) =>
        new VariableLimitAnalyzer().Analyze(context);

    [Fact]
    public void GuardRejectingValuesOutsideARange_ReportsThatRange()
    {
        var limits = Analyze(Context(
            """
            public void Operate(int level)
            {
                if (level < 0 || level > 100) throw new ArgumentOutOfRangeException(nameof(level));
            }
            """,
            parameters: ["int level"]));

        var limit = Assert.Single(limits, l => l.Name == "level");
        Assert.Equal("0 to 100", limit.Limit);
        Assert.Equal(VariableLimitSource.Guard, limit.Source);
        Assert.Equal(AnalysisConfidence.High, limit.Confidence);
    }

    [Fact]
    public void GuardUsingInclusiveComparisons_ExcludesTheRejectedBoundaries()
    {
        // "<= 0" rejects 0 itself, so the lowest permitted value is 1; "> = 5" likewise gives 4.
        var limits = Analyze(Context(
            """
            public void Operate(int rating)
            {
                if (rating <= 0 || rating >= 5) throw new ArgumentException("bad");
            }
            """,
            parameters: ["int rating"]));

        Assert.Equal("1 to 4", Assert.Single(limits, l => l.Name == "rating").Limit);
    }

    [Fact]
    public void ClampCall_ReportsTheRangeItForces()
    {
        var limits = Analyze(Context(
            """
            public void Operate(int score)
            {
                score = Math.Clamp(score, 1, 5);
            }
            """,
            parameters: ["int score"]));

        var limit = Assert.Single(limits, l => l.Name == "score");
        Assert.Equal("1 to 5", limit.Limit);
        Assert.Equal(VariableLimitSource.Clamp, limit.Source);
    }

    [Fact]
    public void CppClampCall_IsRecognisedToo()
    {
        var limits = Analyze(Context(
            """
            void Operate(int volume)
            {
                volume = std::clamp(volume, 0, 11);
            }
            """,
            parameters: ["int volume"],
            fileName: "engine.cpp"));

        Assert.Equal("0 to 11", Assert.Single(limits, l => l.Name == "volume").Limit);
    }

    [Fact]
    public void CharacterComparison_ReportsTheRangeAsCharactersNotNumbers()
    {
        var limits = Analyze(Context(
            """
            public void Operate(char letter)
            {
                if (letter >= 'a' && letter <= 'z') Accept(letter);
            }
            """,
            parameters: ["char letter"]));

        var limit = Assert.Single(limits, l => l.Name == "letter");
        Assert.Equal("'a' to 'z'", limit.Limit);
    }

    [Fact]
    public void ComparisonsAtBothEnds_ProduceARangeWithLowerConfidenceThanAGuard()
    {
        var limits = Analyze(Context(
            """
            public void Operate(int month)
            {
                if (month >= 1 && month <= 12) Use(month);
            }
            """,
            parameters: ["int month"]));

        var limit = Assert.Single(limits, l => l.Name == "month");
        Assert.Equal("1 to 12", limit.Limit);
        Assert.Equal(VariableLimitSource.Comparison, limit.Source);
        Assert.Equal(AnalysisConfidence.Medium, limit.Confidence);
    }

    [Fact]
    public void ComparisonAtOnlyOneEnd_ReportsThatOneEnd()
    {
        var limits = Analyze(Context(
            """
            public void Operate(int count)
            {
                if (count > 0) Use(count);
            }
            """,
            parameters: ["int count"]));

        Assert.Equal("greater than 0", Assert.Single(limits, l => l.Name == "count").Limit);
    }

    [Fact]
    public void GuardRejectingNonPositiveValues_ReportsGreaterThanZero()
    {
        // The guard says what is refused, so refusing "<= 0" permits everything above 0.
        var limits = Analyze(Context(
            """
            public void Operate(int quantity)
            {
                if (quantity <= 0) throw new ArgumentOutOfRangeException(nameof(quantity));
            }
            """,
            parameters: ["int quantity"]));

        var limit = Assert.Single(limits, l => l.Name == "quantity");
        Assert.Equal("greater than 0", limit.Limit);
        Assert.Equal(VariableLimitSource.Guard, limit.Source);
    }

    [Fact]
    public void GuardRejectingNegativeValues_LeavesZeroPermitted()
    {
        var limits = Analyze(Context(
            """
            public void Operate(int offset)
            {
                if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
            }
            """,
            parameters: ["int offset"]));

        Assert.Equal("0 or greater", Assert.Single(limits, l => l.Name == "offset").Limit);
    }

    [Fact]
    public void GuardOnTextLength_ReportsACharacterLimit()
    {
        var limits = Analyze(Context(
            """
            public void Operate(string name)
            {
                if (name.Length > 10) throw new ArgumentException("too long");
            }
            """,
            parameters: ["string name"]));

        var limit = Assert.Single(limits, l => l.Name == "name");
        Assert.Equal("at most 10 characters", limit.Limit);
    }

    [Fact]
    public void GuardOnCollectionSize_CountsItemsRatherThanCharacters()
    {
        var limits = Analyze(Context(
            """
            public void Operate(List<int> values)
            {
                if (values.Count < 3) throw new ArgumentException("too few");
            }
            """,
            parameters: ["List<int> values"]));

        Assert.Equal("at least 3 items", Assert.Single(limits, l => l.Name == "values").Limit);
    }

    [Fact]
    public void NullGuard_BecomesTheLimitOnAReference()
    {
        var limits = Analyze(Context(
            """
            public void Operate(Order order)
            {
                if (order == null) throw new ArgumentNullException(nameof(order));
            }
            """,
            parameters: ["Order order"]));

        Assert.Equal("must not be null", Assert.Single(limits, l => l.Name == "order").Limit);
    }

    [Fact]
    public void AGuardAndAPlainComparisonAreReadOppositeWaysRound()
    {
        // The guard rejects "< 18", so 18 and above is permitted. Read the same way as a plain
        // comparison it would come out as "less than 18" — the exact inverse of the truth.
        var guarded = Analyze(Context(
            """
            public void Operate(int age)
            {
                if (age < 18) throw new ArgumentOutOfRangeException(nameof(age));
            }
            """,
            parameters: ["int age"]));

        Assert.Equal("18 or greater", Assert.Single(guarded, l => l.Name == "age").Limit);

        var compared = Analyze(Context(
            """
            public void Operate(int age)
            {
                if (age < 18) ApplyDiscount(age);
            }
            """,
            parameters: ["int age"]));

        Assert.Equal("less than 18", Assert.Single(compared, l => l.Name == "age").Limit);
    }

    [Fact]
    public void CountingLoop_ReportsTheCounterRangeWithTheLastValueIncluded()
    {
        var limits = Analyze(Context(
            """
            public void Operate()
            {
                for (int i = 0; i < 10; i++) Use(i);
            }
            """));

        // "i < 10" means the counter never reaches 10, so the last value it holds is 9.
        Assert.Equal("0 to 9", Assert.Single(limits, l => l.Name == "i").Limit);
    }

    [Fact]
    public void LoopWithAVariableEnd_ReportsNothing()
    {
        // There is no fixed number to show the reader, so claiming a range would be misleading.
        var limits = Analyze(Context(
            """
            public void Operate(int n)
            {
                for (int i = 0; i < n; i++) Use(i);
            }
            """,
            parameters: ["int n"]));

        Assert.DoesNotContain(limits, l => l.Name == "i");
    }

    [Fact]
    public void NarrowNumericType_ReportsItsNaturalRangeWhenNothingElseIsKnown()
    {
        var limits = Analyze(Context(
            """
            public void Operate(byte channel)
            {
                Use(channel);
            }
            """,
            parameters: ["byte channel"]));

        var limit = Assert.Single(limits, l => l.Name == "channel");
        Assert.Equal("0 to 255", limit.Limit);
        Assert.Equal(VariableLimitSource.DeclaredType, limit.Source);
        Assert.Equal(AnalysisConfidence.Low, limit.Confidence);
    }

    [Fact]
    public void EveryVariableGetsAnAnswer_EvenWhenNothingRestrictsIt()
    {
        // A blank entry would leave the reader unable to tell "unrestricted" from "not examined",
        // so an unrestricted variable falls back to what its type permits.
        var limits = Analyze(Context(
            """
            public void Operate(int total, double ratio, string label, bool active)
            {
                Use(total, ratio, label, active);
            }
            """,
            parameters: ["int total", "double ratio", "string label", "bool active"]));

        Assert.Equal(4, limits.Count);
        Assert.Equal("any whole number", Assert.Single(limits, l => l.Name == "total").Limit);
        Assert.Equal("any decimal number", Assert.Single(limits, l => l.Name == "ratio").Limit);
        Assert.Equal("any text", Assert.Single(limits, l => l.Name == "label").Limit);
        Assert.Equal("true or false", Assert.Single(limits, l => l.Name == "active").Limit);
        Assert.All(limits, l => Assert.Equal(AnalysisConfidence.Low, l.Confidence));
    }

    [Fact]
    public void UnsignedTypes_AreNotDescribedAsAllowingNegatives()
    {
        // size_t is unsigned; calling it "any whole number" would overstate what it accepts.
        var limits = Analyze(Context(
            "void Operate(size_t index) { Use(index); }",
            parameters: ["size_t index"],
            fileName: "engine.cpp"));

        Assert.Equal("0 or greater", Assert.Single(limits).Limit);
    }

    [Fact]
    public void WideNumericTypes_AreDescribedRatherThanQuotedInFull()
    {
        // "-2,147,483,648 to 2,147,483,647" is technically the range of an int and tells the
        // reader nothing; it would also crowd out the variables carrying a real restriction.
        var limits = Analyze(Context(
            "public void Operate(int total) { Use(total); }",
            parameters: ["int total"]));

        Assert.DoesNotContain("2,147,483,647", Assert.Single(limits).Limit, StringComparison.Ordinal);
    }

    [Fact]
    public void ClassFieldsTheMethodNeverTouches_AreLeftOut()
    {
        // Listing every field of the class against every method would bury the ones that matter.
        var parentClass = new ClassInfo { Name = "Sample", SourceFilePath = @"C:\proj\Sample.cs" };
        parentClass.Fields.Add(new VariableInfo { Name = "usedField", Type = "byte" });
        parentClass.Fields.Add(new VariableInfo { Name = "unusedField", Type = "byte" });

        var method = new MethodInfo { Name = "Operate", ParentClass = parentClass };
        method.XmlDocTags["sourceCode"] = "public void Operate() { Use(usedField); }";
        parentClass.Methods.Add(method);

        var limits = Analyze(new MethodAnalysisContext(method));

        Assert.Contains(limits, l => l.Name == "usedField");
        Assert.DoesNotContain(limits, l => l.Name == "unusedField");
    }

    [Fact]
    public void AGuardBeatsTheDeclaredType_SoOnlyTheStrongerFactIsShown()
    {
        var limits = Analyze(Context(
            """
            public void Operate(byte channel)
            {
                if (channel < 1 || channel > 16) throw new ArgumentOutOfRangeException(nameof(channel));
            }
            """,
            parameters: ["byte channel"]));

        var limit = Assert.Single(limits, l => l.Name == "channel");
        Assert.Equal("1 to 16", limit.Limit);
        Assert.Equal(VariableLimitSource.Guard, limit.Source);
    }

    [Fact]
    public void EveryLimitQuotesTheCodeItWasReadFrom()
    {
        var limits = Analyze(Context(
            """
            public void Operate(int level)
            {
                if (level < 0 || level > 100) throw new ArgumentOutOfRangeException(nameof(level));
            }
            """,
            parameters: ["int level"]));

        // Without evidence the reader cannot tell an inference from a fact.
        Assert.All(limits, l => Assert.False(string.IsNullOrWhiteSpace(l.Evidence)));
        Assert.Contains("level", Assert.Single(limits).Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void WordsThatAreNotVariables_AreIgnored()
    {
        // "timeout" is never declared, so a comparison mentioning it must not become a limit.
        var limits = Analyze(Context(
            """
            public void Operate(int level)
            {
                if (timeout >= 1 && timeout <= 30) Wait();
            }
            """,
            parameters: ["int level"]));

        Assert.DoesNotContain(limits, l => l.Name == "timeout");
    }

    [Fact]
    public void LocalVariablesAreCovered_NotJustParameters()
    {
        var limits = Analyze(Context(
            """
            public void Operate()
            {
                int retries = 0;
                if (retries < 1 || retries > 3) throw new InvalidOperationException("bad");
            }
            """,
            locals: [("retries", "int")]));

        var limit = Assert.Single(limits, l => l.Name == "retries");
        Assert.Equal("1 to 3", limit.Limit);
        Assert.Equal(VariableScopeKind.Local, limit.Scope);
    }

    [Fact]
    public void MethodWithNoStoredBody_StillReportsWhatTheSignatureShows()
    {
        // The body is what reveals a restriction, but the parameter types are known regardless,
        // so the reader still gets an answer rather than an empty panel.
        var parentClass = new ClassInfo { Name = "Sample", SourceFilePath = @"C:\proj\Sample.cs" };
        var method = new MethodInfo { Name = "Operate", ParentClass = parentClass };
        method.Parameters.Add("byte channel");
        parentClass.Methods.Add(method);

        var limit = Assert.Single(Analyze(new MethodAnalysisContext(method)));
        Assert.Equal("0 to 255", limit.Limit);
        Assert.Equal(VariableLimitSource.DeclaredType, limit.Source);
    }

    [Fact]
    public void DecimalLiteralsWithATypeSuffix_AreRead()
    {
        // Taken from the project's own test fixture. Real C# writes 0m, 1.5f, 10L, and a rule
        // that only understands whole numbers silently reports nothing for any of them.
        var limits = Analyze(Context(
            """
            public void Operate(decimal rate)
            {
                if (rate < 0m || rate > 1m) throw new ArgumentOutOfRangeException(nameof(rate));
            }
            """,
            parameters: ["decimal rate"]));

        Assert.Equal("0 to 1", Assert.Single(limits, l => l.Name == "rate").Limit);
    }

    [Fact]
    public void FractionalBounds_AreKeptRatherThanRounded()
    {
        var limits = Analyze(Context(
            """
            public void Operate(double ratio)
            {
                if (ratio < 0.5 || ratio > 2.75) throw new ArgumentOutOfRangeException(nameof(ratio));
            }
            """,
            parameters: ["double ratio"]));

        Assert.Equal("0.5 to 2.75", Assert.Single(limits, l => l.Name == "ratio").Limit);
    }

    [Fact]
    public void AnExcludedFractionalBoundary_IsWordedRatherThanShiftedByOne()
    {
        // With whole numbers "> 0" can be reported as "1 or more". With fractions there is no
        // next value, so stating one would be a lie; the wording has to carry the meaning.
        var limits = Analyze(Context(
            """
            public void Operate(double ratio)
            {
                if (ratio <= 0.5 || ratio >= 2.5) throw new ArgumentOutOfRangeException(nameof(ratio));
            }
            """,
            parameters: ["double ratio"]));

        var limit = Assert.Single(limits, l => l.Name == "ratio");
        Assert.Equal("greater than 0.5, less than 2.5", limit.Limit);
    }

    [Fact]
    public void OneVariableNeverProducesTwoCompetingLimits()
    {
        // The clamp and the comparisons both describe "score"; the reader must be given one answer.
        var limits = Analyze(Context(
            """
            public void Operate(int score)
            {
                if (score >= 0 && score <= 50) Log(score);
                score = Math.Clamp(score, 1, 5);
            }
            """,
            parameters: ["int score"]));

        Assert.Single(limits, l => l.Name == "score");
    }
}

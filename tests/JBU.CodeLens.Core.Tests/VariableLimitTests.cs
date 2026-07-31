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
    public void ComparisonAtOnlyOneEnd_ReportsNothing()
    {
        // A single bound is not a range, and guessing the other end would be inventing a fact.
        var limits = Analyze(Context(
            """
            public void Operate(int count)
            {
                if (count > 0) Use(count);
            }
            """,
            parameters: ["int count"]));

        Assert.DoesNotContain(limits, l => l.Name == "count");
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
    public void IntAndDouble_AreNotReportedFromTheirTypeAlone()
    {
        // Quoting the full range of an int tells the reader nothing they did not already know,
        // and would bury the variables that carry a real restriction.
        var limits = Analyze(Context(
            """
            public void Operate(int total, double ratio)
            {
                Use(total, ratio);
            }
            """,
            parameters: ["int total", "double ratio"]));

        Assert.Empty(limits);
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
    public void MethodWithNoStoredBody_ReportsNothingRatherThanGuessing()
    {
        var parentClass = new ClassInfo { Name = "Sample", SourceFilePath = @"C:\proj\Sample.cs" };
        var method = new MethodInfo { Name = "Operate", ParentClass = parentClass };
        method.Parameters.Add("byte channel");
        parentClass.Methods.Add(method);

        Assert.Empty(Analyze(new MethodAnalysisContext(method)));
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
        Assert.Equal("more than 0.5, less than 2.5", limit.Limit);
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

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
    public void ASingleComparisonInABranch_IsNotTreatedAsALimit()
    {
        // Passing 0 here is perfectly legal; the method simply does nothing with it. Found in
        // this project's own source, where "if (angle > 0)" tests the result of IndexOf, a
        // value that is routinely -1, so "greater than 0" would have been a plain falsehood.
        var limits = Analyze(Context(
            """
            public void Operate(int count)
            {
                if (count > 0) Use(count);
            }
            """,
            parameters: ["int count"]));

        Assert.Equal("any whole number", Assert.Single(limits, l => l.Name == "count").Limit);
    }

    [Fact]
    public void ASingleBoundIsStillReportedWhenTheMethodActuallyRefusesIt()
    {
        // The difference from the test above is the throw: this method will not accept 0.
        var limits = Analyze(Context(
            """
            public void Operate(int count)
            {
                if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
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
        // A guard names what it refuses, so refusing "< 18" permits 18 and above. Read at face
        // value, as a comparison is, it would come out as "less than 18", the exact inverse.
        var guarded = Analyze(Context(
            """
            public void Operate(int age)
            {
                if (age < 18) throw new ArgumentOutOfRangeException(nameof(age));
            }
            """,
            parameters: ["int age"]));

        Assert.Equal("18 or greater", Assert.Single(guarded, l => l.Name == "age").Limit);

        // A comparison names what the code works with, and is taken as written.
        var compared = Analyze(Context(
            """
            public void Operate(int age)
            {
                if (age >= 18 && age <= 65) ApplyDiscount(age);
            }
            """,
            parameters: ["int age"]));

        Assert.Equal("18 to 65", Assert.Single(compared, l => l.Name == "age").Limit);
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
    public void ComplementaryFactsAreCombined_NotChosenBetween()
    {
        // Being present and being short are both true at once. Reporting only one would hide
        // half of what the method actually enforces.
        var limits = Analyze(Context(
            """
            public void Operate(string name)
            {
                if (name == null) throw new ArgumentNullException(nameof(name));
                if (name.Length > 50) throw new ArgumentException("too long");
            }
            """,
            parameters: ["string name"]));

        var limit = Assert.Single(limits, l => l.Name == "name");
        Assert.Equal("must not be null, at most 50 characters", limit.Limit);
    }

    [Fact]
    public void PresenceIsStatedBeforeRange()
    {
        // A value that may be absent has to be dealt with before its range is even a question.
        var limits = Analyze(Context(
            """
            public void Operate(Order order, int count)
            {
                if (order == null) throw new ArgumentNullException(nameof(order));
                if (count < 1 || count > 10) throw new ArgumentOutOfRangeException(nameof(count));
            }
            """,
            parameters: ["Order order", "int count"]));

        Assert.StartsWith("must not be null", Assert.Single(limits, l => l.Name == "order").Limit,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheTypeRangeIsDroppedOnceSomethingNarrowerIsKnown()
    {
        var limits = Analyze(Context(
            """
            public void Operate(byte channel)
            {
                if (channel > 15) throw new ArgumentOutOfRangeException(nameof(channel));
            }
            """,
            parameters: ["byte channel"]));

        // "0 to 255" would only dilute the real finding.
        var limit = Assert.Single(limits, l => l.Name == "channel");
        Assert.Equal("15 or less", limit.Limit);
    }

    [Fact]
    public void ADivisorMustNotBeZero_EvenWithNoGuardInTheSource()
    {
        // Dividing by zero is a fault whether or not anyone checked for it, so the division is
        // itself the evidence.
        var limits = Analyze(Context(
            """
            public int Operate(int total, int divisor)
            {
                return total / divisor;
            }
            """,
            parameters: ["int total", "int divisor"]));

        Assert.Contains("must not be zero", Assert.Single(limits, l => l.Name == "divisor").Limit,
            StringComparison.Ordinal);
        Assert.DoesNotContain("must not be zero", Assert.Single(limits, l => l.Name == "total").Limit,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ANamedConstantBound_IsFollowedToItsValue()
    {
        // "count > MaxItems" is how real code writes a bound. Reporting the range of the type
        // would throw away the one number the reader wants.
        var parentClass = new ClassInfo { Name = "Sample", SourceFilePath = @"C:\proj\Sample.cs" };
        parentClass.Fields.Add(new VariableInfo { Name = "MaxItems", Type = "int", InitialValue = "100" });

        var method = new MethodInfo { Name = "Operate", ParentClass = parentClass };
        method.Parameters.Add("int count");
        method.XmlDocTags["sourceCode"] =
            """
            public void Operate(int count)
            {
                if (count > MaxItems) throw new ArgumentOutOfRangeException(nameof(count));
            }
            """;
        parentClass.Methods.Add(method);

        var limit = Assert.Single(Analyze(new MethodAnalysisContext(method)), l => l.Name == "count");
        Assert.Equal("100 or less", limit.Limit);

        // The name still appears in the evidence, so the number can be traced back to it.
        Assert.Contains("MaxItems", limit.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void ANameThatIsReassigned_IsNotTreatedAsAConstant()
    {
        // It is initialised to a literal but does not stand for one, so quoting 5 would state a
        // bound the method never applies. The initial value has to be set for this to exercise
        // the reassignment check at all, without it, resolution never starts and the test would
        // pass for the wrong reason.
        var parentClass = new ClassInfo { Name = "Sample", SourceFilePath = @"C:\proj\Sample.cs" };
        var method = new MethodInfo { Name = "Operate", ParentClass = parentClass };
        method.Parameters.Add("int count");
        method.LocalVariables.Add(new VariableInfo { Name = "limit", Type = "int", InitialValue = "5" });
        method.XmlDocTags["sourceCode"] =
            """
            public void Operate(int count)
            {
                int limit = 5;
                limit = Compute();
                if (count > limit) throw new ArgumentOutOfRangeException(nameof(count));
            }
            """;
        parentClass.Methods.Add(method);

        var limits = Analyze(new MethodAnalysisContext(method));
        var limit = Assert.Single(limits, l => l.Name == "count");
        Assert.Equal("any whole number", limit.Limit);
        Assert.DoesNotContain("5", limit.Limit, StringComparison.Ordinal);
    }

    [Fact]
    public void ALocalConstantBound_IsFollowedToItsValue()
    {
        var parentClass = new ClassInfo { Name = "Sample", SourceFilePath = @"C:\proj\Sample.cs" };
        var method = new MethodInfo { Name = "Operate", ParentClass = parentClass };
        method.Parameters.Add("int count");
        method.LocalVariables.Add(new VariableInfo { Name = "cap", Type = "int", InitialValue = "8" });
        method.XmlDocTags["sourceCode"] =
            """
            public void Operate(int count)
            {
                const int cap = 8;
                if (count > cap) throw new ArgumentOutOfRangeException(nameof(count));
            }
            """;
        parentClass.Methods.Add(method);

        Assert.Equal("8 or less",
            Assert.Single(Analyze(new MethodAnalysisContext(method)), l => l.Name == "count").Limit);
    }

    [Fact]
    public void AClampBoundedByNamedConstants_FollowsThemToTheirValues()
    {
        // The guard rules were taught to follow constants but the clamp rule was left behind, so
        // the same bound reported a range when written as a guard and nothing when clamped.
        var parentClass = new ClassInfo { Name = "Sample", SourceFilePath = @"C:\proj\Sample.cs" };
        parentClass.Fields.Add(new VariableInfo { Name = "MinLevel", Type = "int", InitialValue = "1" });
        parentClass.Fields.Add(new VariableInfo { Name = "MaxLevel", Type = "int", InitialValue = "9" });

        var method = new MethodInfo { Name = "Operate", ParentClass = parentClass };
        method.Parameters.Add("int level");
        method.XmlDocTags["sourceCode"] =
            """
            public void Operate(int level)
            {
                level = Math.Clamp(level, MinLevel, MaxLevel);
            }
            """;
        parentClass.Methods.Add(method);

        Assert.Equal(
            "1 to 9",
            Assert.Single(Analyze(new MethodAnalysisContext(method)), l => l.Name == "level").Limit);
    }

    [Fact]
    public void AGuardWhoseConditionWrapsOntoASecondLine_IsStillRead()
    {
        // Long conditions get wrapped by every formatter. Matching one physical line at a time
        // meant such a guard was not seen at all.
        var limits = Analyze(Context(
            """
            public void Operate(int level)
            {
                if (level < 0 ||
                    level > 100)
                {
                    throw new ArgumentOutOfRangeException(nameof(level));
                }
            }
            """,
            parameters: ["int level"]));

        Assert.Equal("0 to 100", Assert.Single(limits, l => l.Name == "level").Limit);
    }

    [Fact]
    public void ADivisorTheMethodChecksAndHandles_IsNotReportedAsRestricted()
    {
        // The method deals with zero itself and returns, so a caller may legitimately pass it.
        // This is the same substitution-versus-refusal distinction applied everywhere else: the
        // division is only a restriction when nothing stands between it and a zero.
        var limits = Analyze(Context(
            """
            public int Operate(int total, int divisor)
            {
                if (divisor == 0) return 0;
                return total / divisor;
            }
            """,
            parameters: ["int total", "int divisor"]));

        Assert.DoesNotContain("zero", Assert.Single(limits, l => l.Name == "divisor").Limit,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ADivisorGuardedByAThrow_IsStillReportedAsRestricted()
    {
        // Refusing zero is not the same as handling it: here the caller really must not pass one.
        var limits = Analyze(Context(
            """
            public int Operate(int total, int divisor)
            {
                if (divisor == 0) throw new DivideByZeroException();
                return total / divisor;
            }
            """,
            parameters: ["int total", "int divisor"]));

        Assert.Contains("must not be zero", Assert.Single(limits, l => l.Name == "divisor").Limit,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AnUncheckedDivisor_IsStillReportedAsRestricted()
    {
        var limits = Analyze(Context(
            "public int Operate(int total, int divisor) { return total / divisor; }",
            parameters: ["int total", "int divisor"]));

        Assert.Contains("must not be zero", Assert.Single(limits, l => l.Name == "divisor").Limit,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DividingByAMemberDoesNotConstrainTheObjectItBelongsTo()
    {
        // Found in this project's own MetricsCalculator: "total / ir.Classes.Count" made ir
        // itself "must not be zero", which is meaningless for an object reference. What must not
        // be zero is the count.
        var limits = Analyze(Context(
            """
            public void Operate(ProjectIR ir, int total)
            {
                var average = total / ir.Classes.Count;
            }
            """,
            parameters: ["ProjectIR ir", "int total"]));

        Assert.DoesNotContain("zero", Assert.Single(limits, l => l.Name == "ir").Limit,
            StringComparison.Ordinal);
    }

    [Fact]
    public void BoundsThatAreVariables_NameThemRatherThanFallingBackToTheType()
    {
        var limits = Analyze(Context(
            """
            public void Operate(int value, int min, int max)
            {
                if (value < min || value > max) throw new ArgumentOutOfRangeException(nameof(value));
            }
            """,
            parameters: ["int value", "int min", "int max"]));

        Assert.Equal("between min and max", Assert.Single(limits, l => l.Name == "value").Limit);
    }

    [Fact]
    public void SymbolicBoundsWrittenAsSeparateStatements_AreStillOneRange()
    {
        // The two ends are separate statements, and a rule expecting them joined by "||" would
        // see neither.
        var limits = Analyze(Context(
            """
            public void Operate(int value, int low, int high)
            {
                if (value < low) throw new ArgumentOutOfRangeException(nameof(value));
                if (value > high) throw new ArgumentOutOfRangeException(nameof(value));
            }
            """,
            parameters: ["int value", "int low", "int high"]));

        Assert.Equal("between low and high", Assert.Single(limits, l => l.Name == "value").Limit);
    }

    [Fact]
    public void AValueTheMethodSubstitutesRatherThanRefuses_IsNotRestricted()
    {
        // clampValue from the project's own C++ fixture. Passing -500 is perfectly legal, the
        // method returns low instead. Reading the early return as a refusal would advertise a
        // restriction callers do not have to obey.
        var limits = Analyze(Context(
            """
            int clampValue(int value, int low, int high) {
                if (value < low) return low;
                if (value > high) return high;
                return value;
            }
            """,
            parameters: ["int value", "int low", "int high"],
            fileName: "engine.cpp"));

        Assert.Equal("any whole number", Assert.Single(limits, l => l.Name == "value").Limit);
    }

    [Fact]
    public void AnIdentifierContainingTheWordReturn_DoesNotMakeALineARefusal()
    {
        // Found in this project's own source: "if (returnStatements.Count > 0)" was read as a
        // refusal because "return" appears inside the variable's name, producing the nonsense
        // limit "at most 0 items".
        var limits = Analyze(Context(
            """
            public void Operate(List<string> returnStatements)
            {
                if (returnStatements.Count > 0) { Use(returnStatements); }
            }
            """,
            parameters: ["List<string> returnStatements"]));

        var limit = Assert.Single(limits, l => l.Name == "returnStatements");
        Assert.DoesNotContain("at most 0", limit.Limit, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEarlyReturnOnNull_DoesNotBecomeMustNotBeNull()
    {
        // "if (x is null) return;" means null is handled, which is the opposite of forbidden.
        var limits = Analyze(Context(
            """
            public void Operate(Order order)
            {
                if (order is null) return;
                Use(order);
            }
            """,
            parameters: ["Order order"]));

        Assert.DoesNotContain("must not be null", Assert.Single(limits).Limit, StringComparison.Ordinal);
    }

    [Fact]
    public void ModernNullChecksAreRecognised()
    {
        // "is null" and ArgumentNullException.ThrowIfNull are how current C# is written; the
        // project's own fixture uses the first of them.
        var isNull = Analyze(Context(
            """
            public void Operate(Order order)
            {
                if (order is null) throw new ArgumentNullException(nameof(order));
            }
            """,
            parameters: ["Order order"]));
        Assert.Equal("must not be null", Assert.Single(isNull, l => l.Name == "order").Limit);

        var throwIfNull = Analyze(Context(
            """
            public void Operate(Order order)
            {
                ArgumentNullException.ThrowIfNull(order);
            }
            """,
            parameters: ["Order order"]));
        Assert.Equal("must not be null", Assert.Single(throwIfNull, l => l.Name == "order").Limit);
    }

    [Fact]
    public void BoundsNamingSomethingThatIsNotAVariable_AreIgnored()
    {
        // Without this the rule would read any two words either side of the operators as bounds.
        var limits = Analyze(Context(
            """
            public void Operate(int value)
            {
                if (value < floor || value > ceiling) throw new ArgumentOutOfRangeException(nameof(value));
            }
            """,
            parameters: ["int value"]));

        Assert.DoesNotContain("between", Assert.Single(limits, l => l.Name == "value").Limit,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AGuardAdmittingOnlyAFewValues_ListsThem()
    {
        var limits = Analyze(Context(
            """
            public void Operate(int mode)
            {
                if (mode != 1 && mode != 2 && mode != 3) throw new ArgumentException("bad mode");
            }
            """,
            parameters: ["int mode"]));

        Assert.Equal("1, 2 or 3", Assert.Single(limits, l => l.Name == "mode").Limit);
    }

    [Fact]
    public void ASingleRejectedValue_IsNotReportedAsASet()
    {
        // "not 5" is not a list of permitted values, and presenting it as one would invert it.
        var limits = Analyze(Context(
            """
            public void Operate(int mode)
            {
                if (mode != 5) throw new ArgumentException("bad mode");
            }
            """,
            parameters: ["int mode"]));

        Assert.Equal("any whole number", Assert.Single(limits, l => l.Name == "mode").Limit);
    }

    [Fact]
    public void TheSameValueRejectedTwice_IsNotTurnedIntoAPermittedValue()
    {
        // Two comparisons satisfy the pattern, but they name one value, and that value is the
        // one being refused. Reporting it as permitted would state the exact opposite.
        var limits = Analyze(Context(
            """
            public void Operate(int mode)
            {
                if (mode != 5 && mode != 5) throw new ArgumentException("bad mode");
            }
            """,
            parameters: ["int mode"]));

        var limit = Assert.Single(limits, l => l.Name == "mode");
        Assert.NotEqual(VariableLimitKind.Membership, limit.Kind);
        Assert.DoesNotContain("5", limit.Limit, StringComparison.Ordinal);
    }

    [Fact]
    public void AGuardWrittenAcrossTwoLines_IsStillRead()
    {
        // Brace style puts the throw on its own line. This is how most C++ is written, and a rule
        // insisting on one physical line missed all of it.
        var limits = Analyze(Context(
            """
            void Operate(int priority, int channelCount) {
                if (channelCount <= 0) {
                    throw std::out_of_range("bad count");
                }
                if (priority != 1 && priority != 2 && priority != 3) {
                    throw std::invalid_argument("bad priority");
                }
            }
            """,
            parameters: ["int priority", "int channelCount"],
            fileName: "engine.cpp"));

        Assert.Equal("greater than 0", Assert.Single(limits, l => l.Name == "channelCount").Limit);
        Assert.Equal("1, 2 or 3", Assert.Single(limits, l => l.Name == "priority").Limit);
    }

    [Fact]
    public void AGuardWithTheBraceOnItsOwnLine_IsRead()
    {
        // Allman style is the ordinary C# convention, and handling only the brace-at-end form
        // missed most real guards: this exact shape reported nothing for compoundsPerYear.
        var limits = Analyze(Context(
            """
            public void Operate(int compoundsPerYear)
            {
                if (compoundsPerYear != 1 && compoundsPerYear != 4 && compoundsPerYear != 12)
                {
                    throw new ArgumentException("Unsupported.", nameof(compoundsPerYear));
                }
            }
            """,
            parameters: ["int compoundsPerYear"]));

        Assert.Equal("1, 4 or 12", Assert.Single(limits, l => l.Name == "compoundsPerYear").Limit);
    }

    [Fact]
    public void TwoSeparateGuards_CombineIntoOneRange()
    {
        // Writing the ends as separate statements is at least as common as joining them with
        // "or". Emitting them one at a time meant only the first survived, so a value guarded at
        // both ends was reported as bounded at one.
        var limits = Analyze(Context(
            """
            public void Operate(int level)
            {
                if (level < 1) throw new ArgumentOutOfRangeException(nameof(level));
                if (level > 100) throw new ArgumentOutOfRangeException(nameof(level));
            }
            """,
            parameters: ["int level"]));

        Assert.Equal("1 to 100", Assert.Single(limits, l => l.Name == "level").Limit);
    }

    [Fact]
    public void SeveralGuardsOnOneEnd_KeepTheTightest()
    {
        var limits = Analyze(Context(
            """
            public void Operate(int level)
            {
                if (level < 1) throw new ArgumentOutOfRangeException(nameof(level));
                if (level < 10) throw new ArgumentOutOfRangeException(nameof(level));
            }
            """,
            parameters: ["int level"]));

        Assert.Equal("10 or greater", Assert.Single(limits, l => l.Name == "level").Limit);
    }

    [Fact]
    public void ACharacterRangeWrittenAsARefusal_IsReadTheSameWay()
    {
        // "accept a to z" and "refuse anything outside a to z" describe one span.
        var limits = Analyze(Context(
            """
            void Operate(char band) {
                if (band < 'a' || band > 'z') {
                    throw std::invalid_argument("bad band");
                }
            }
            """,
            parameters: ["char band"],
            fileName: "radio.cpp"));

        Assert.Equal("'a' to 'z'", Assert.Single(limits, l => l.Name == "band").Limit);
    }

    [Fact]
    public void AFixedLengthCheck_ReportsTheExactSize()
    {
        var limits = Analyze(Context(
            """
            public void Operate(string iban)
            {
                if (iban.Length != 22)
                {
                    throw new ArgumentException("Wrong length.", nameof(iban));
                }
            }
            """,
            parameters: ["string iban"]));

        Assert.Equal("exactly 22 characters", Assert.Single(limits, l => l.Name == "iban").Limit);
    }

    [Fact]
    public void AnEmptinessCheckOnACollection_ReadsAsAMinimumOfOne()
    {
        // And singular: "at least 1 items" is the sort of thing a reader notices immediately.
        var limits = Analyze(Context(
            """
            public void Operate(List<int> batch)
            {
                if (batch.Count == 0)
                {
                    throw new ArgumentException("Empty.", nameof(batch));
                }
            }
            """,
            parameters: ["List<int> batch"]));

        Assert.Equal("at least 1 item", Assert.Single(limits, l => l.Name == "batch").Limit);
    }

    [Fact]
    public void AConditionOpeningAnOrdinaryBlock_IsNotJoinedToALaterThrow()
    {
        // Only the first statement of the block can be the refusal. Without that rule the
        // condition of any branch would pair with whatever throw happened to follow it.
        var limits = Analyze(Context(
            """
            void Operate(int count, int other) {
                if (count > 5) {
                    Log(count);
                    Adjust(other);
                }
                if (other <= 0) {
                    throw new ArgumentException("bad");
                }
            }
            """,
            parameters: ["int count", "int other"]));

        Assert.Equal("any whole number", Assert.Single(limits, l => l.Name == "count").Limit);
        Assert.Equal("greater than 0", Assert.Single(limits, l => l.Name == "other").Limit);
    }

    [Fact]
    public void CommentedOutCode_IsNotReadAsIfItRan()
    {
        // A limit taken from a line that was deliberately disabled would be worse than no limit:
        // it states a restriction the running code does not apply.
        var limits = Analyze(Context(
            """
            public void Operate(int level)
            {
                // if (level < 0 || level > 100) throw new ArgumentOutOfRangeException(nameof(level));
                Use(level);
            }
            """,
            parameters: ["int level"]));

        Assert.Equal("any whole number", Assert.Single(limits, l => l.Name == "level").Limit);
    }

    [Fact]
    public void ABlockCommentIsIgnoredToo()
    {
        var limits = Analyze(Context(
            """
            public void Operate(int level)
            {
                /* if (level < 1 || level > 9) throw new ArgumentException("x"); */
                Use(level);
            }
            """,
            parameters: ["int level"]));

        Assert.Equal("any whole number", Assert.Single(limits, l => l.Name == "level").Limit);
    }

    [Fact]
    public void AMentionOfDivisionInACommentDoesNotMakeSomethingADivisor()
    {
        var limits = Analyze(Context(
            """
            public void Operate(int total)
            {
                // rate is computed elsewhere as amount / total
                Use(total);
            }
            """,
            parameters: ["int total"]));

        Assert.DoesNotContain("zero", Assert.Single(limits, l => l.Name == "total").Limit,
            StringComparison.Ordinal);
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

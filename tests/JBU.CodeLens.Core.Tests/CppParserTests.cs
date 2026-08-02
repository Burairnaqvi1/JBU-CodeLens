using JBU.CodeLens.Core.Parsing.Cpp;
using JBU.CodeLens.Shared.Models;

namespace JBU.CodeLens.Core.Tests;

/// <summary>
/// End-to-end tests for the libclang-backed C++ parser. Each test writes a real file and runs
/// the real native parse, so they also guard the P/Invoke marshaling contract.
/// </summary>
public sealed class CppParserTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("codelens-cpp-tests").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private ParseResult Parse(string fileName, string content)
    {
        var path = Path.Combine(_tempDir, fileName);
        File.WriteAllText(path, content);
        return new CppParser().Parse(path);
    }

    [Fact]
    public void Parse_ClassInsideNamespace_IsDiscovered()
    {
        var result = Parse("ns.cpp", """
            namespace acme {
                class Engine {
                public:
                    int Start(int rpm) { return rpm * 2; }
                };
            }
            """);

        Assert.Empty(result.Errors);
        var engine = Assert.Single(result.Classes, c => c.Name == "Engine");
        Assert.Single(engine.Methods, m => m.Name == "Start");
    }

    [Fact]
    public void Parse_ClassInNestedNamespace_IsDiscovered()
    {
        var result = Parse("nested_ns.cpp", """
            namespace outer { namespace inner {
                struct Widget {
                    void Render() {}
                };
            } }
            """);

        Assert.Contains(result.Classes, c => c.Name == "Widget");
    }

    [Fact]
    public void Parse_TopLevelClass_IsDiscovered()
    {
        var result = Parse("plain.cpp", """
            class Calculator {
            public:
                double Divide(double a, double b) { return a / b; }
            };
            """);

        var cls = Assert.Single(result.Classes, c => c.Name == "Calculator");
        Assert.Single(cls.Methods, m => m.Name == "Divide");
    }

    [Fact]
    public void Parse_FreeFunctionInsideNamespace_IsDiscovered()
    {
        var result = Parse("ns_free.cpp", """
            namespace util {
                int Add(int a, int b) { return a + b; }
            }
            """);

        var global = Assert.Single(result.Classes, c => c.Name == "(global)");
        Assert.Contains(global.Methods, m => m.Name == "Add");
    }

    [Fact]
    public void Parse_OverloadedMethods_KeepsAllOverloads()
    {
        var result = Parse("overloads.cpp", """
            class Printer {
            public:
                void Print(int value) {}
                void Print(double value, int precision) {}
            };
            """);

        var printer = Assert.Single(result.Classes, c => c.Name == "Printer");
        Assert.Equal(2, printer.Methods.Count(m => m.Name == "Print"));
    }

    [Fact]
    public void Parse_NonAsciiPath_ParsesSuccessfully()
    {
        var subDir = Path.Combine(_tempDir, "प्रोजेक्ट-ü");
        Directory.CreateDirectory(subDir);
        var path = Path.Combine(subDir, "café.cpp");
        File.WriteAllText(path, """
            class Näive {
            public:
                int Compute() { return 42; }
            };
            """);

        var result = new CppParser().Parse(path);

        Assert.Empty(result.Errors);
        var cls = Assert.Single(result.Classes, c => c.Name.StartsWith('N'));
        Assert.Single(cls.Methods, m => m.Name == "Compute");
    }

    [Fact]
    public void Parse_NonAsciiContent_ExtractsAlignedSourceText()
    {
        // The comment before the method contains multi-byte characters; if byte offsets from
        // libclang are applied to the UTF-16 string, the extracted method source is shifted.
        var result = Parse("unicode_body.cpp", """
            // Überprüfung: größer, schöner, weiß — 日本語のコメント
            class Sample {
            public:
                int GetValue() { return 7; }
            };
            """);

        var cls = Assert.Single(result.Classes, c => c.Name == "Sample");
        var method = Assert.Single(cls.Methods, m => m.Name == "GetValue");
        Assert.True(method.XmlDocTags.TryGetValue("sourceCode", out var source));
        Assert.StartsWith("int GetValue", source.Trim(), StringComparison.Ordinal);
        Assert.Contains("return 7;", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_BranchingFunction_ReportsCyclomaticComplexity()
    {
        // Complexity was never computed for C++ at all, so every method sat at the default of 1.
        // A C++ project therefore reported an average complexity of 1.0 however involved it was,
        // and no C++ method could reach the "most complex methods" list.
        var result = Parse("branching.cpp", """
            int classify(int score, bool strict) {
                if (score < 0) { return -1; }
                if (score > 90) { return 4; }
                else if (score > 70) { return 3; }
                else if (score > 50) { return 2; }
                for (int i = 0; i < 3; ++i) { score += i; }
                return strict ? 1 : 0;
            }
            """);

        Assert.Empty(result.Errors);
        var method = result.Classes.SelectMany(c => c.Methods).Single(m => m.Name == "classify");

        // One for the method, plus four ifs, a for and a conditional expression.
        Assert.Equal(7, method.CyclomaticComplexity);
    }

    [Fact]
    public void Parse_LongMethod_IsAnalysedToItsEnd()
    {
        // The stored body used to be cut at 800 characters, which truncated the analysis rather
        // than the display: branches past the cut went uncounted and any guard or division there
        // was invisible. Padding pushes the final branch beyond that old limit.
        var padding = string.Join("\n", Enumerable.Range(0, 60).Select(i => $"    int filler{i} = {i};"));
        var result = Parse("long.cpp", $$"""
            int summarise(int total, int divisor) {
            {{padding}}
                if (divisor == 0) { return 0; }
                if (total > 100) { total = 100; }
                return total / divisor;
            }
            """);

        Assert.Empty(result.Errors);
        var method = result.Classes.SelectMany(c => c.Methods).Single(m => m.Name == "summarise");

        Assert.True(
            method.XmlDocTags["sourceCode"].Length > 800,
            "the fixture must exceed the old cap for this test to mean anything");
        Assert.Equal(3, method.CyclomaticComplexity);
    }

    [Fact]
    public void Parse_NamespaceScopeConstant_IsAvailableToTheEnclosingType()
    {
        // C++ commonly writes a bound as a namespace-scope constant. Without collecting these,
        // a guard against one has no value to resolve to and the limit stops at whichever end
        // happens to be a literal.
        var result = Parse("consts.cpp", """
            namespace acme {
                const int MaxWindow = 64;
                int widen(int window) {
                    if (window <= 0 || window > MaxWindow) { return 0; }
                    return window;
                }
            }
            """);

        Assert.Empty(result.Errors);
        var field = result.Classes
            .SelectMany(c => c.Fields)
            .Single(f => f.Name == "MaxWindow");

        Assert.Equal("64", field.InitialValue);
    }

    [Fact]
    public void Parse_ComputedConstant_IsNotGivenAValue()
    {
        // A constant derived from something else has no fixed literal to quote, and a guess
        // would be worse than leaving the bound unresolved.
        var result = Parse("computed.cpp", """
            namespace acme {
                int baseSize();
                const int Derived = baseSize() * 2;
                int use(int n) { return n; }
            }
            """);

        var field = result.Classes.SelectMany(c => c.Fields).FirstOrDefault(f => f.Name == "Derived");
        Assert.True(field is null || field.InitialValue is null, "a computed constant must not carry a literal");
    }

    [Fact]
    public void Parse_ThrowingFunction_ListsTheExceptionTypes()
    {
        // The C# side listed a method's exceptions while C++ always reported none, so the errors
        // section told the reader a throwing function threw nothing.
        var result = Parse("throws.cpp", """
            #include <stdexcept>
            int checked(int value) {
                if (value < 0) { throw std::invalid_argument("negative"); }
                if (value > 10) { throw std::out_of_range("too big"); }
                return value;
            }
            """);

        var method = result.Classes.SelectMany(c => c.Methods).Single(m => m.Name == "checked");
        Assert.Contains("std::invalid_argument", method.ThrownExceptions, StringComparer.Ordinal);
        Assert.Contains("std::out_of_range", method.ThrownExceptions, StringComparer.Ordinal);
    }

    [Fact]
    public void Complexity_CountsShortCircuitOperators()
    {
        // "a && b" can leave the condition by two routes, and McCabe counts both.
        var result = Parse("shortcircuit.cpp", """
            int check(int a, int b) {
                if (a > 0 && b > 0) { return 1; }
                return 0;
            }
            """);

        var method = result.Classes.SelectMany(c => c.Methods).Single(m => m.Name == "check");

        // One for the method, one for the if, one for the &&.
        Assert.Equal(3, method.CyclomaticComplexity);
    }

    [Fact]
    public void Complexity_CountsNestedTernariesSeparately()
    {
        // The pattern used before tried to match "? ... :" and lost count once two were nested.
        var result = Parse("ternary.cpp", """
            int band(int n) { return n > 10 ? 2 : n > 5 ? 1 : 0; }
            """);

        var method = result.Classes.SelectMany(c => c.Methods).Single(m => m.Name == "band");

        // One for the method plus one for each of the two conditionals.
        Assert.Equal(3, method.CyclomaticComplexity);
    }

    /// <summary>
    /// The same logic written in each language must produce the same figure.
    /// </summary>
    /// <remarks>
    /// The two counters share no code — one walks a syntax tree, the other matches patterns —
    /// so nothing but a test stops them drifting apart. A project-wide average mixing the two
    /// languages would then mean nothing, and neither would a comparison between them.
    /// </remarks>
    [Theory]
    [InlineData("if (a > 0) { return 1; } return 0;", 2)]
    [InlineData("if (a > 0 && b > 0) { return 1; } return 0;", 3)]
    [InlineData("if (a > 0 || b > 0) { return 1; } return 0;", 3)]
    [InlineData("for (int i = 0; i < 3; ++i) { a += i; } return a;", 2)]
    [InlineData("while (a > 0) { a--; } return a;", 2)]
    [InlineData("return a > 0 ? 1 : 0;", 2)]
    [InlineData("if (a > 0) { if (b > 0) { return 2; } return 1; } return 0;", 3)]
    [InlineData("return 0;", 1)]
    public void Complexity_AgreesBetweenTheTwoLanguages(string body, int expected)
    {
        var cpp = Parse("parity.cpp", $"int probe(int a, int b) {{ {body} }}")
            .Classes.SelectMany(c => c.Methods).Single(m => m.Name == "probe");

        var csharpPath = Path.Combine(_tempDir, "parity.cs");
        File.WriteAllText(csharpPath, $"public class P {{ public int Probe(int a, int b) {{ {body} }} }}");
        var csharp = new JBU.CodeLens.Core.Parsing.CSharp.CSharpParser().Parse(csharpPath)
            .Classes.SelectMany(c => c.Methods).Single(m => m.Name == "Probe");

        Assert.Equal(expected, cpp.CyclomaticComplexity);
        Assert.Equal(expected, csharp.CyclomaticComplexity);
    }

    [Fact]
    public void Parse_SimpleFunction_HasComplexityOfOne()
    {
        var result = Parse("plain.cpp", """
            int twice(int value) { return value * 2; }
            """);

        var method = result.Classes.SelectMany(c => c.Methods).Single(m => m.Name == "twice");
        Assert.Equal(1, method.CyclomaticComplexity);
    }

    [Fact]
    public void Parse_FreeFunctionTakingAQualifiedType_IsNotFiledUnderAPhantomClass()
    {
        // The owning type was taken from the last "::" anywhere in the display name, which found
        // the one inside std::vector and invented a class called "average(const std".
        var result = Parse("freefn.cpp", """
            #include <vector>
            int average(const std::vector<int>& samples, int fallback) {
                if (samples.empty()) { return fallback; }
                return samples[0];
            }
            """);

        Assert.Empty(result.Errors);

        // The phantom name was the function's own name with half a parameter type attached.
        Assert.DoesNotContain(result.Classes, c => c.Name.Contains("std", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Classes, c => c.Name.StartsWith("average", StringComparison.Ordinal));

        var global = Assert.Single(result.Classes, c => c.Name == "(global)");
        Assert.Single(global.Methods, m => m.Name == "average");
    }
}

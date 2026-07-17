using CodeLensAI.Core.Parsing.Cpp;
using CodeLensAI.Shared.Models;

namespace CodeLensAI.Core.Tests;

/// <summary>
/// End-to-end tests for the libclang-backed C++ parser. Each test writes a real file and runs
/// the real native parse, so they also guard the P/Invoke marshaling contract.
/// </summary>
public class CppParserTests : IDisposable
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
        var cls = Assert.Single(result.Classes, c => c.Name.StartsWith("N"));
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
        Assert.StartsWith("int GetValue", source!.Trim());
        Assert.Contains("return 7;", source);
    }
}

# Parser Module

## C# Parser (SCIDE.Parser.CSharp)

Uses Roslyn (`Microsoft.CodeAnalysis.CSharp` 4.9.2) for full syntax tree analysis.

**File:** `src/SCIDE.Parser.CSharp/RoslynParser.cs`

### Extraction Capabilities

- Class, interface, struct, enum declarations
- Method signatures (return type, name, parameters, modifiers)
- Properties and fields with types and modifiers
- XML documentation comments (summary tags)
- Method call references (via `InvocationExpressionSyntax`)
- Cyclomatic complexity (branch counting: `if`, `for`, `while`, `switch case`, `&&`, `||`, `catch`)
- Base type and implemented interfaces

### Usage

```csharp
var parser = new RoslynParser();
var types = parser.Parse(file); // file is ProjectFile
```

Returns `List<TypeInfo>` from a single file.

## C++ Parser (SCIDE.Parser.Cpp)

Uses regex-based analysis as a portable fallback.

**File:** `src/SCIDE.Parser.Cpp/CppParser.cs`

### Extraction Capabilities

- Class declarations (name, base class)
- Method declarations (return type, name, parameters)  
- Field declarations
- Inheritance detection (`class Foo : public Bar`)

### Limitations

- No XML doc comments (C++ has no standard equivalent)
- No call graph extraction
- Limited complexity analysis

ClangSharp is referenced as an optional dependency for future enhancement.

## Adding a New Language

1. Create a new project (e.g. `SCIDE.Parser.Java`)
2. Implement `IParser` interface:

```csharp
public class JavaParser : IParser
{
    public string Language => "Java";
    public List<TypeInfo> Parse(ProjectFile file) { /* ... */ }
}
```

3. Register in `ScideEngine`:

```csharp
_parsers.Register("java", new JavaParser());
```

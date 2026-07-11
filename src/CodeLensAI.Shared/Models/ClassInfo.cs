namespace CodeLensAI.Shared.Models;
/// <summary>
/// Describes a single class discovered in a source file, including its documentation summary
/// and the members (methods and properties) declared directly inside it.
/// </summary>
public class ClassInfo
{
    /// <summary>
    /// The class's identifier name (without namespace or type parameters).
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The namespace this class is declared inside, or an empty string when it has none
    /// (e.g. top-level C# types, or C++ classes, which aren't namespace-tracked by the parser).
    /// </summary>
    public string NamespaceName { get; set; } = string.Empty;

    /// <summary>
    /// The role this class plays in the codebase, assigned by <see cref="CategoryClassifier"/>.
    /// Defaults to <see cref="CodeCategory.BusinessLogic"/>.
    /// </summary>
    public CodeCategory Category { get; set; } = CodeCategory.BusinessLogic;

    /// <summary>
    /// The text of the class's <c>/// &lt;summary&gt;</c> XML documentation comment, or
    /// <c>null</c> when the class has no such comment.
    /// </summary>
    public string? XmlSummary { get; set; }

    /// <summary>
    /// The name of the class this class derives from, or <c>null</c> when it has no base class.
    /// Because C# has no multiple class inheritance, there is at most one base class.
    /// </summary>
    public string? BaseClassName { get; set; }

    /// <summary>
    /// The names of the interfaces this class implements, as inferred from its base list.
    /// </summary>
    public List<string> ImplementedInterfaces { get; set; } = new();

    /// <summary>
    /// The distinct non-primitive type names this class references through its properties and
    /// fields (composition). Generic wrapper types such as <c>List</c> are unwrapped to their
    /// type arguments (for example, <c>List&lt;Engine&gt;</c> contributes <c>Engine</c>).
    /// </summary>
    public List<string> Dependencies { get; set; } = new();

    /// <summary>
    /// The methods declared directly within this class.
    /// </summary>
    public List<MethodInfo> Methods { get; set; } = new();

    /// <summary>
    /// The properties declared directly within this class.
    /// </summary>
    public List<PropertyInfo> Properties { get; set; } = new();

    /// <summary>
    /// Class-level fields discovered by the parser (used as global variables in method analysis).
    /// </summary>
    public List<VariableInfo> Fields { get; set; } = new();

    /// <summary>
    /// The source file path this class was parsed from.
    /// </summary>
    public string SourceFilePath { get; set; } = string.Empty;
}

/// <summary>
/// A variable discovered in source — either a class field or a method-local declaration.
/// </summary>
public class VariableInfo
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string? InitialValue { get; set; }
    public bool IsField { get; set; }
    public string AccessModifier { get; set; } = "private";
}

/// <summary>
/// Describes a single method declaration: its signature pieces, documentation summary, and
/// declared access level.
/// </summary>
public class MethodInfo
{
    /// <summary>
    /// The method's identifier name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The method's return type as written in source (for example, <c>void</c> or <c>bool</c>).
    /// </summary>
    public string ReturnType { get; set; } = string.Empty;

    /// <summary>
    /// The method's parameters, each formatted as <c>"Type name"</c> (for example,
    /// <c>"string filePath"</c>).
    /// </summary>
    public List<string> Parameters { get; set; } = new();

    /// <summary>
    /// The text of the method's <c>/// &lt;summary&gt;</c> XML documentation comment, or
    /// <c>null</c> when absent.
    /// </summary>
    public string? XmlSummary { get; set; }

    /// <summary>
    /// The method's access modifier (<c>public</c>, <c>private</c>, <c>protected</c>, or
    /// <c>internal</c>). Defaults to <c>private</c> when no access modifier is written, matching
    /// C# member defaults.
    /// </summary>
    public string AccessModifier { get; set; } = "private";

    /// <summary>
    /// Parsed XML documentation tags keyed by tag name (for example <c>summary</c>,
    /// <c>returns</c>, <c>param:name</c>, <c>exception:TypeName</c>).
    /// </summary>
    public Dictionary<string, string> XmlDocTags { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Exception type names detected in <c>throw</c> statements within the method body.
    /// </summary>
    public List<string> ThrownExceptions { get; set; } = new();

    /// <summary>
    /// Method/function call expressions found in this method's body (used to build the
    /// project-wide call graph in <see cref="Structural.ScideEngine"/>). Populated for C#;
    /// left empty for C++, matching the parser's existing capability split.
    /// </summary>
    public List<string> CalledMethodNames { get; set; } = new();

    /// <summary>
    /// Cyclomatic complexity (1 + number of branching constructs in the body). Computed for C#;
    /// defaults to 1 for C++, matching the parser's existing capability split.
    /// </summary>
    public int CyclomaticComplexity { get; set; } = 1;

    /// <summary>
    /// The class that declares this method. Set during parsing for relationship context in the UI.
    /// </summary>
    public ClassInfo? ParentClass { get; set; }

    /// <summary>
    /// Local variables declared inside the method body.
    /// </summary>
    public List<VariableInfo> LocalVariables { get; set; } = new();

    /// <summary>
    /// Value constraints inferred from guard clauses and comparisons in the method body.
    /// </summary>
    public List<string> OperationalLimits { get; set; } = new();

    /// <summary>
    /// Roslyn syntax node for C# methods, set during parsing for reuse by analyzers. Typed as <see cref="object"/> so this shared DTO assembly carries no Roslyn dependency; Core consumers cast it back to <c>MethodDeclarationSyntax</c>.
    /// </summary>
    public object? SyntaxNode { get; set; }

    /// <summary>
    /// Pre-computed deterministic analysis from the inference engine (set during SCIDE scan).
    /// </summary>
    public MethodAnalysis? CachedAnalysis { get; set; }

    /// <summary>
    /// AI-generated brief description, cached after the first successful inference so revisiting
    /// the method in the UI doesn't re-run the model. Reset by a rescan (methods are re-created).
    /// </summary>
    public string? CachedAiBriefDescription { get; set; }
}

/// <summary>
/// Describes a single property declaration: its name, type, documentation summary, and
/// declared access level.
/// </summary>
public class PropertyInfo
{
    /// <summary>
    /// The property's identifier name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The property's type as written in source (for example, <c>int</c> or <c>string</c>).
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// The text of the property's <c>/// &lt;summary&gt;</c> XML documentation comment, or
    /// <c>null</c> when absent.
    /// </summary>
    public string? XmlSummary { get; set; }

    /// <summary>
    /// The property's access modifier (<c>public</c>, <c>private</c>, <c>protected</c>, or
    /// <c>internal</c>). Defaults to <c>private</c> when no access modifier is written, matching
    /// C# member defaults.
    /// </summary>
    public string AccessModifier { get; set; } = "private";
}

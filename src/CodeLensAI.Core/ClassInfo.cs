namespace CodeLensAI.Core;

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

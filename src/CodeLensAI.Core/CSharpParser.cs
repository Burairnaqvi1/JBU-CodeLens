using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeLensAI.Core;

/// <summary>
/// An <see cref="ILanguageParser"/> for C# source files, backed by the Roslyn
/// (<c>Microsoft.CodeAnalysis.CSharp</c>) syntax APIs. It performs a purely syntactic parse,
/// so it does not require the project to compile or any references to be resolved.
/// </summary>
public class CSharpParser : ILanguageParser
{
    /// <summary>
    /// Reads and parses a C# file, returning the top-level classes it declares along with their
    /// methods, properties, and any XML documentation summaries.
    /// </summary>
    /// <remarks>
    /// Only classes that sit directly inside the compilation unit or a namespace are reported;
    /// classes nested inside other types are intentionally ignored for now. Likewise, only the
    /// methods and properties declared directly within each class are collected. Any I/O or
    /// parsing failure is captured in <see cref="ParseResult.Errors"/> rather than thrown, so
    /// callers can safely run this across many files in a loop.
    /// </remarks>
    /// <param name="filePath">Path to the C# file to parse.</param>
    /// <returns>A <see cref="ParseResult"/> with the discovered classes and any errors.</returns>
    public ParseResult Parse(string filePath)
    {
        var result = new ParseResult { FilePath = filePath };

        try
        {
            var sourceText = File.ReadAllText(filePath);
            var syntaxTree = CSharpSyntaxTree.ParseText(sourceText);
            var root = syntaxTree.GetCompilationUnitRoot();

            foreach (var classDeclaration in GetTopLevelClasses(root))
            {
                result.Classes.Add(BuildClassInfo(classDeclaration));
            }
        }
        catch (Exception ex)
        {
            result.Errors.Add($"Failed to parse '{filePath}': {ex.Message}");
        }

        return result;
    }

    /// <summary>
    /// Yields the class declarations that are direct members of the compilation unit or of a
    /// namespace (both classic block <c>namespace { }</c> and file-scoped <c>namespace;</c>
    /// forms), excluding classes nested within other type declarations.
    /// </summary>
    private static IEnumerable<ClassDeclarationSyntax> GetTopLevelClasses(CompilationUnitSyntax root)
    {
        foreach (var member in root.Members)
        {
            switch (member)
            {
                case ClassDeclarationSyntax topLevelClass:
                    yield return topLevelClass;
                    break;

                case BaseNamespaceDeclarationSyntax namespaceDeclaration:
                    foreach (var namespaceMember in namespaceDeclaration.Members)
                    {
                        if (namespaceMember is ClassDeclarationSyntax classInNamespace)
                        {
                            yield return classInNamespace;
                        }
                    }

                    break;
            }
        }
    }

    /// <summary>
    /// Maps a <see cref="ClassDeclarationSyntax"/> to a <see cref="ClassInfo"/>, collecting the
    /// class summary and the methods and properties declared directly within it.
    /// </summary>
    private static ClassInfo BuildClassInfo(ClassDeclarationSyntax classDeclaration)
    {
        var classInfo = new ClassInfo
        {
            Name = classDeclaration.Identifier.Text,
            XmlSummary = ExtractXmlSummary(classDeclaration),
        };

        ApplyBaseList(classDeclaration, classInfo);

        foreach (var member in classDeclaration.Members)
        {
            switch (member)
            {
                case MethodDeclarationSyntax method:
                    classInfo.Methods.Add(BuildMethodInfo(method));
                    break;

                case PropertyDeclarationSyntax property:
                    classInfo.Properties.Add(BuildPropertyInfo(property));
                    CollectTypeNames(property.Type, classInfo.Dependencies);
                    break;

                case FieldDeclarationSyntax field:
                    CollectTypeNames(field.Declaration.Type, classInfo.Dependencies);
                    break;
            }
        }

        return classInfo;
    }

    /// <summary>
    /// Splits the class's base list into a single base class and any implemented interfaces.
    /// </summary>
    /// <remarks>
    /// Roslyn's syntax model does not distinguish a base class from an interface in the base
    /// list (both are just <see cref="BaseTypeSyntax"/> entries), because that distinction needs
    /// semantic/symbol information. We apply a naming heuristic instead: a type whose simple name
    /// is <c>I</c> followed by an uppercase letter (for example, <c>IDisposable</c>) is treated as
    /// an interface; anything else is treated as the base class. Since C# permits at most one base
    /// class, only the first non-interface entry is recorded.
    /// </remarks>
    private static void ApplyBaseList(ClassDeclarationSyntax classDeclaration, ClassInfo classInfo)
    {
        if (classDeclaration.BaseList is null)
        {
            return;
        }

        foreach (var baseType in classDeclaration.BaseList.Types)
        {
            var name = GetSimpleTypeName(baseType.Type);
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            if (LooksLikeInterface(name))
            {
                classInfo.ImplementedInterfaces.Add(name);
            }
            else
            {
                classInfo.BaseClassName ??= name;
            }
        }
    }

    /// <summary>
    /// Applies the interface naming convention: an identifier of <c>I</c> immediately followed by
    /// an uppercase letter is assumed to be an interface.
    /// </summary>
    private static bool LooksLikeInterface(string name)
    {
        return name.Length >= 2 && name[0] == 'I' && char.IsUpper(name[1]);
    }

    /// <summary>
    /// Maps a <see cref="MethodDeclarationSyntax"/> to a <see cref="MethodInfo"/>, formatting each
    /// parameter as <c>"Type name"</c>.
    /// </summary>
    private static MethodInfo BuildMethodInfo(MethodDeclarationSyntax method)
    {
        var methodInfo = new MethodInfo
        {
            Name = method.Identifier.Text,
            ReturnType = method.ReturnType.ToString(),
            XmlSummary = ExtractXmlSummary(method),
            AccessModifier = GetAccessModifier(method.Modifiers),
        };

        foreach (var parameter in method.ParameterList.Parameters)
        {
            var type = parameter.Type?.ToString() ?? "var";
            methodInfo.Parameters.Add($"{type} {parameter.Identifier.Text}");
        }

        return methodInfo;
    }

    /// <summary>
    /// Maps a <see cref="PropertyDeclarationSyntax"/> to a <see cref="PropertyInfo"/>.
    /// </summary>
    private static PropertyInfo BuildPropertyInfo(PropertyDeclarationSyntax property)
    {
        return new PropertyInfo
        {
            Name = property.Identifier.Text,
            Type = property.Type.ToString(),
            XmlSummary = ExtractXmlSummary(property),
            AccessModifier = GetAccessModifier(property.Modifiers),
        };
    }

    /// <summary>
    /// Returns the access modifier keyword present in <paramref name="modifiers"/>, or
    /// <c>"private"</c> when none is specified (matching the C# default for class members).
    /// A declared <c>internal</c> wins only if no other access keyword is present; an
    /// <c>protected internal</c> pair is reported as <c>protected</c> for simplicity.
    /// </summary>
    private static string GetAccessModifier(SyntaxTokenList modifiers)
    {
        if (modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)))
        {
            return "public";
        }

        if (modifiers.Any(m => m.IsKind(SyntaxKind.ProtectedKeyword)))
        {
            return "protected";
        }

        if (modifiers.Any(m => m.IsKind(SyntaxKind.PrivateKeyword)))
        {
            return "private";
        }

        if (modifiers.Any(m => m.IsKind(SyntaxKind.InternalKeyword)))
        {
            return "internal";
        }

        return "private";
    }

    /// <summary>
    /// Type names that are treated as primitives/built-ins and therefore excluded from the
    /// dependency list. Keyword forms (<c>int</c>, <c>string</c>, ...) are already filtered as
    /// <see cref="PredefinedTypeSyntax"/>; this set also covers their qualified framework names.
    /// </summary>
    private static readonly HashSet<string> PrimitiveTypeNames = new(StringComparer.Ordinal)
    {
        "string", "int", "bool", "void", "object", "char", "byte", "sbyte",
        "short", "ushort", "uint", "long", "ulong", "float", "double", "decimal",
        "nint", "nuint", "dynamic", "var",
        "String", "Int32", "Boolean", "Object", "Char", "Byte", "Int16", "Int64",
        "Double", "Single", "Decimal", "Void",
    };

    /// <summary>
    /// Returns the simple (rightmost, unqualified) identifier of a type, dropping any namespace
    /// qualifier and generic type-argument list. For example, <c>System.Collections.Generic.List</c>
    /// and <c>List&lt;T&gt;</c> both yield <c>List</c>.
    /// </summary>
    private static string GetSimpleTypeName(TypeSyntax type)
    {
        return type switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.Text,
            GenericNameSyntax generic => generic.Identifier.Text,
            QualifiedNameSyntax qualified => GetSimpleTypeName(qualified.Right),
            AliasQualifiedNameSyntax alias => GetSimpleTypeName(alias.Name),
            NullableTypeSyntax nullable => GetSimpleTypeName(nullable.ElementType),
            ArrayTypeSyntax array => GetSimpleTypeName(array.ElementType),
            _ => type.ToString(),
        };
    }

    /// <summary>
    /// Walks a type reference and adds the non-primitive type names it contributes to
    /// <paramref name="dependencies"/>, deduplicating as it goes. Generic types are unwrapped to
    /// their type arguments (so <c>List&lt;Engine&gt;</c> contributes <c>Engine</c>, not
    /// <c>List</c>); nullable, array, and tuple types are likewise unwrapped to their element types.
    /// </summary>
    private static void CollectTypeNames(TypeSyntax type, List<string> dependencies)
    {
        switch (type)
        {
            case PredefinedTypeSyntax:
                return;

            case GenericNameSyntax generic:
                foreach (var argument in generic.TypeArgumentList.Arguments)
                {
                    CollectTypeNames(argument, dependencies);
                }

                return;

            case QualifiedNameSyntax qualified:
                CollectTypeNames(qualified.Right, dependencies);
                return;

            case AliasQualifiedNameSyntax alias:
                CollectTypeNames(alias.Name, dependencies);
                return;

            case NullableTypeSyntax nullable:
                CollectTypeNames(nullable.ElementType, dependencies);
                return;

            case ArrayTypeSyntax array:
                CollectTypeNames(array.ElementType, dependencies);
                return;

            case TupleTypeSyntax tuple:
                foreach (var element in tuple.Elements)
                {
                    CollectTypeNames(element.Type, dependencies);
                }

                return;

            case IdentifierNameSyntax identifier:
                AddDependency(identifier.Identifier.Text, dependencies);
                return;
        }
    }

    /// <summary>
    /// Adds <paramref name="name"/> to <paramref name="dependencies"/> unless it is a primitive
    /// type or already present (preserving first-seen order).
    /// </summary>
    private static void AddDependency(string name, List<string> dependencies)
    {
        if (string.IsNullOrEmpty(name) || PrimitiveTypeNames.Contains(name))
        {
            return;
        }

        if (!dependencies.Contains(name))
        {
            dependencies.Add(name);
        }
    }

    /// <summary>
    /// Extracts the text of a <c>&lt;summary&gt;</c> element from the XML documentation comment
    /// (<c>///</c>) that precedes <paramref name="node"/>, or <c>null</c> when the node has no
    /// documentation comment or no summary element.
    /// </summary>
    /// <remarks>
    /// Documentation comments are attached to a node as <em>leading trivia</em>. This method finds
    /// the <see cref="DocumentationCommentTriviaSyntax"/> structure of that trivia, locates the
    /// <c>&lt;summary&gt;</c> <see cref="XmlElementSyntax"/>, and joins its text content into a
    /// single normalized line.
    /// </remarks>
    private static string? ExtractXmlSummary(SyntaxNode node)
    {
        var documentationComment = node
            .GetLeadingTrivia()
            .Select(trivia => trivia.GetStructure())
            .OfType<DocumentationCommentTriviaSyntax>()
            .FirstOrDefault();

        if (documentationComment is null)
        {
            return null;
        }

        var summaryElement = documentationComment.Content
            .OfType<XmlElementSyntax>()
            .FirstOrDefault(element => element.StartTag.Name.LocalName.Text == "summary");

        if (summaryElement is null)
        {
            return null;
        }

        var builder = new StringBuilder();
        foreach (var textNode in summaryElement.Content.OfType<XmlTextSyntax>())
        {
            foreach (var token in textNode.TextTokens)
            {
                builder.Append(token.ValueText);
            }
        }

        var normalized = NormalizeWhitespace(builder.ToString());
        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }

    /// <summary>
    /// Collapses runs of whitespace (including the newlines between <c>///</c> lines) into single
    /// spaces and trims the ends, turning a multi-line summary into one clean line.
    /// </summary>
    private static string NormalizeWhitespace(string text)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', parts);
    }
}

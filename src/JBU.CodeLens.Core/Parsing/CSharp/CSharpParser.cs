using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace JBU.CodeLens.Core.Parsing.CSharp;

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
    /// Every type (class, record, struct or interface) in the file is reported, including those
    /// nested inside another type, which appear under a qualified name such as
    /// <c>Outer.Inner</c>. Methods, constructors, properties and fields declared directly within
    /// each type are collected. Any I/O or parsing failure is captured in
    /// <see cref="ParseResult.Errors"/> rather than thrown, so callers can safely run this across
    /// many files in a loop.
    /// </remarks>
    /// <param name="filePath">Path to the C# file to parse.</param>
    /// <returns>A <see cref="ParseResult"/> with the discovered classes and any errors.</returns>
    public ParseResult Parse(string filePath)
    {
        try
        {
            return ParseSource(File.ReadAllText(filePath), filePath);
        }
        catch (Exception ex)
        {
            var result = new ParseResult { FilePath = filePath };
            result.Errors.Add($"Failed to parse '{filePath}': {ex.Message}");
            return result;
        }
    }

    /// <inheritdoc />
    public async Task<ParseResult> ParseAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            var sourceText = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
            return ParseSource(sourceText, filePath);
        }
        catch (Exception ex)
        {
            var result = new ParseResult { FilePath = filePath };
            result.Errors.Add($"Failed to parse '{filePath}': {ex.Message}");
            return result;
        }
    }

    private static ParseResult ParseSource(string sourceText, string filePath)
    {
        var result = new ParseResult { FilePath = filePath };

        try
        {
            var syntaxTree = CSharpSyntaxTree.ParseText(sourceText);
            var root = syntaxTree.GetCompilationUnitRoot();

            foreach (var (typeDeclaration, namespaceName, namePrefix) in GetTopLevelTypes(root.Members, string.Empty))
            {
                result.Classes.Add(BuildClassInfo(typeDeclaration, filePath, namespaceName, namePrefix));
            }
        }
        catch (Exception ex)
        {
            result.Errors.Add($"Failed to parse '{filePath}': {ex.Message}");
        }

        return result;
    }

    /// <summary>
    /// Yields every type declaration in the file — classes, records (including record structs),
    /// structs and interfaces — whether declared directly in the compilation unit, inside a
    /// namespace in any of its forms, or nested within another type.
    /// </summary>
    /// <remarks>
    /// Nested types are reported under a qualified name such as <c>Outer.Inner</c>, so the tree
    /// shows where they live. They were previously skipped, which meant a type nested inside
    /// another was absent from the tree, the exported documents and every measurement, with
    /// nothing to tell the reader anything had been left out — silence that reads as "there is
    /// nothing there" rather than "this was not examined".
    /// </remarks>
    private static IEnumerable<(TypeDeclarationSyntax Type, string NamespaceName, string NamePrefix)> GetTopLevelTypes(
        SyntaxList<MemberDeclarationSyntax> members, string namespacePrefix, string namePrefix = "")
    {
        foreach (var member in members)
        {
            switch (member)
            {
                case TypeDeclarationSyntax typeDeclaration:
                    yield return (typeDeclaration, namespacePrefix, namePrefix);

                    var containerName = namePrefix + typeDeclaration.Identifier.Text + ".";
                    foreach (var nested in GetTopLevelTypes(
                        typeDeclaration.Members, namespacePrefix, containerName))
                    {
                        yield return nested;
                    }

                    break;

                case BaseNamespaceDeclarationSyntax namespaceDeclaration:
                    var name = namespacePrefix.Length == 0
                        ? namespaceDeclaration.Name.ToString()
                        : $"{namespacePrefix}.{namespaceDeclaration.Name}";
                    foreach (var nested in GetTopLevelTypes(namespaceDeclaration.Members, name))
                    {
                        yield return nested;
                    }

                    break;
            }
        }
    }

    /// <summary>
    /// Maps a <see cref="TypeDeclarationSyntax"/> to a <see cref="ClassInfo"/>, collecting the
    /// type summary and the methods and properties declared directly within it. Positional
    /// record parameters are reported as public properties, which is what they compile to.
    /// </summary>
    private static ClassInfo BuildClassInfo(
        TypeDeclarationSyntax typeDeclaration,
        string filePath,
        string namespaceName,
        string namePrefix = "")
    {
        var xmlDoc = ExtractXmlDocumentation(typeDeclaration);
        var classInfo = new ClassInfo
        {
            Name = namePrefix + typeDeclaration.Identifier.Text,
            NamespaceName = namespaceName,
            XmlSummary = xmlDoc.GetValueOrDefault("summary"),
            SourceFilePath = filePath,
        };

        ApplyBaseList(typeDeclaration, classInfo);

        if (typeDeclaration is RecordDeclarationSyntax { ParameterList: { } recordParameters })
        {
            foreach (var parameter in recordParameters.Parameters)
            {
                classInfo.Properties.Add(new PropertyInfo
                {
                    Name = parameter.Identifier.Text,
                    Type = parameter.Type?.ToString() ?? "var",
                    AccessModifier = "public",
                });

                if (parameter.Type is not null)
                {
                    CollectTypeNames(parameter.Type, classInfo.Dependencies);
                }
            }
        }

        foreach (var member in typeDeclaration.Members)
        {
            switch (member)
            {
                case MethodDeclarationSyntax method:
                    classInfo.Methods.Add(BuildMethodInfo(method, classInfo));
                    break;

                case ConstructorDeclarationSyntax constructor:
                    classInfo.Methods.Add(BuildMethodInfo(constructor, classInfo));
                    break;

                case PropertyDeclarationSyntax property:
                    classInfo.Properties.Add(BuildPropertyInfo(property));
                    CollectTypeNames(property.Type, classInfo.Dependencies);
                    break;

                case FieldDeclarationSyntax field:
                    foreach (var variable in field.Declaration.Variables)
                    {
                        classInfo.Fields.Add(new VariableInfo
                        {
                            Name = variable.Identifier.Text,
                            Type = field.Declaration.Type.ToString(),
                            InitialValue = variable.Initializer?.Value.ToString(),
                            IsField = true,
                            AccessModifier = GetAccessModifier(field.Modifiers),
                        });
                    }

                    CollectTypeNames(field.Declaration.Type, classInfo.Dependencies);
                    break;
            }
        }

        classInfo.Category = CategoryClassifier.Classify(classInfo);
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
    private static void ApplyBaseList(TypeDeclarationSyntax typeDeclaration, ClassInfo classInfo)
    {
        if (typeDeclaration.BaseList is null)
        {
            return;
        }

        foreach (var baseType in typeDeclaration.BaseList.Types)
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
    private static MethodInfo BuildMethodInfo(MethodDeclarationSyntax method, ClassInfo parentClass) =>
        BuildMethodInfo(method, parentClass, method.Identifier.Text, method.ReturnType.ToString());

    /// <summary>
    /// Maps a constructor to a <see cref="MethodInfo"/>, so it is measured and described like any
    /// other member.
    /// </summary>
    /// <remarks>
    /// Constructors were previously skipped altogether, so a long one with real branching
    /// contributed nothing to the complexity figures, never appeared in the tree, and had no
    /// value limits worked out for its parameters — despite being the one member every caller of
    /// the type has to get right. The return type is recorded as the type being built, which is
    /// what a constructor produces.
    /// </remarks>
    private static MethodInfo BuildMethodInfo(ConstructorDeclarationSyntax constructor, ClassInfo parentClass) =>
        BuildMethodInfo(constructor, parentClass, constructor.Identifier.Text, parentClass.Name);

    private static MethodInfo BuildMethodInfo(
        BaseMethodDeclarationSyntax declaration,
        ClassInfo parentClass,
        string name,
        string returnType)
    {
        var xmlDoc = ExtractXmlDocumentation(declaration);
        var methodInfo = new MethodInfo
        {
            Name = name,
            ReturnType = returnType,
            XmlSummary = xmlDoc.GetValueOrDefault("summary"),
            XmlDocTags = xmlDoc,
            AccessModifier = GetAccessModifier(declaration.Modifiers),
            ParentClass = parentClass,
        };

        foreach (var parameter in declaration.ParameterList.Parameters)
        {
            var type = parameter.Type?.ToString() ?? "var";
            methodInfo.Parameters.Add($"{type} {parameter.Identifier.Text}");
        }

        CollectBodyFacts(declaration, methodInfo);
        methodInfo.SyntaxNode = declaration;

        // Expose the method body to the deterministic analyzers. Without this, every source-body
        // based rule (pre/post conditions, runtime risks, design constraints, dependencies) is
        // skipped for C#, because MethodAnalysisContext reads the body from XmlDocTags["sourceCode"].
        var bodyText = declaration.Body?.ToString() ?? declaration.ExpressionBody?.ToString();
        if (!string.IsNullOrWhiteSpace(bodyText))
        {
            methodInfo.XmlDocTags["sourceCode"] = bodyText;
        }

        return methodInfo;
    }

    /// <summary>
    /// Walks the method body <b>once</b> and derives every body-based fact in a single pass:
    /// called method names, cyclomatic complexity, thrown exception types, local variables, and
    /// guard-clause operational limits. Replaces eight separate <c>DescendantNodes()</c>
    /// enumerations per method (calls, complexity, throws ×2, locals ×2, limits ×2).
    /// </summary>
    private static void CollectBodyFacts(BaseMethodDeclarationSyntax method, MethodInfo methodInfo)
    {
        var calls = new List<string>();
        var exceptions = new List<string>();
        var locals = new List<VariableInfo>();
        var limits = new List<string>();
        var complexity = 1;

        foreach (var node in method.DescendantNodes())
        {
            if (node.IsKind(SyntaxKind.IfStatement) ||
                node.IsKind(SyntaxKind.WhileStatement) ||
                node.IsKind(SyntaxKind.ForStatement) ||
                node.IsKind(SyntaxKind.ForEachStatement) ||
                node.IsKind(SyntaxKind.CaseSwitchLabel) ||
                node.IsKind(SyntaxKind.ConditionalExpression) ||
                node.IsKind(SyntaxKind.CatchClause))
            {
                complexity++;
            }

            switch (node)
            {
                case InvocationExpressionSyntax invocation:
                    var expr = invocation.Expression.ToString();
                    if (!string.IsNullOrEmpty(expr) && !calls.Contains(expr))
                    {
                        calls.Add(expr);
                    }

                    if (expr.Contains("ThrowIfNull", StringComparison.Ordinal) ||
                        expr.Contains("ThrowIfNullOrEmpty", StringComparison.Ordinal) ||
                        expr.Contains("ThrowIfNullOrWhiteSpace", StringComparison.Ordinal))
                    {
                        var argument = invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression.ToString();
                        if (!string.IsNullOrEmpty(argument))
                        {
                            AddLimit(limits, $"{argument} must not be null or empty");
                        }
                    }

                    break;

                case ThrowStatementSyntax throwStatement:
                    AddThrownType(exceptions, GetThrownExceptionType(throwStatement.Expression));
                    break;

                case ThrowExpressionSyntax throwExpression:
                    AddThrownType(exceptions, GetThrownExceptionType(throwExpression.Expression));
                    break;

                case LocalDeclarationStatementSyntax localDecl:
                    var type = localDecl.Declaration.Type.ToString();
                    foreach (var variable in localDecl.Declaration.Variables)
                    {
                        locals.Add(new VariableInfo
                        {
                            Name = variable.Identifier.Text,
                            Type = type,
                            InitialValue = variable.Initializer?.Value.ToString(),
                            IsField = false,
                        });
                    }

                    break;

                case DeclarationExpressionSyntax outArg
                    when outArg.Designation is SingleVariableDesignationSyntax designation:
                    locals.Add(new VariableInfo
                    {
                        Name = designation.Identifier.Text,
                        Type = outArg.Type.ToString(),
                        IsField = false,
                    });
                    break;

                case IfStatementSyntax ifStatement when ContainsThrow(ifStatement.Statement):
                    var condition = NormalizeWhitespace(ifStatement.Condition.ToString());
                    if (!string.IsNullOrEmpty(condition))
                    {
                        AddLimit(limits, condition);
                    }

                    break;
            }
        }

        methodInfo.CalledMethodNames = calls;
        methodInfo.ThrownExceptions = exceptions;
        methodInfo.LocalVariables = locals;
        methodInfo.OperationalLimits = limits;
        methodInfo.CyclomaticComplexity = complexity;
    }

    /// <summary>
    /// Maps a <see cref="PropertyDeclarationSyntax"/> to a <see cref="PropertyInfo"/>.
    /// </summary>
    private static PropertyInfo BuildPropertyInfo(PropertyDeclarationSyntax property)
    {
        var xmlDoc = ExtractXmlDocumentation(property);
        return new PropertyInfo
        {
            Name = property.Identifier.Text,
            Type = property.Type.ToString(),
            XmlSummary = xmlDoc.GetValueOrDefault("summary"),
            AccessModifier = GetAccessModifier(property.Modifiers),
        };
    }

    private static bool ContainsThrow(StatementSyntax statement)
    {
        return statement.DescendantNodesAndSelf().Any(node =>
            node is ThrowStatementSyntax or ThrowExpressionSyntax);
    }

    private static void AddLimit(List<string> limits, string description)
    {
        var formatted = description.StartsWith("When ", StringComparison.OrdinalIgnoreCase)
            ? description
            : $"When {description}";

        if (!limits.Contains(formatted, StringComparer.OrdinalIgnoreCase))
        {
            limits.Add(formatted);
        }
    }

    private static void AddThrownType(List<string> exceptions, string? typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
        {
            return;
        }

        if (!exceptions.Contains(typeName, StringComparer.Ordinal))
        {
            exceptions.Add(typeName);
        }
    }

    /// <summary>
    /// Resolves the exception type name from a throw expression (for example
    /// <c>new IOException()</c> or a re-thrown identifier).
    /// </summary>
    private static string? GetThrownExceptionType(ExpressionSyntax? expression)
    {
        return expression switch
        {
            ObjectCreationExpressionSyntax objectCreation => GetSimpleTypeName(objectCreation.Type),
            IdentifierNameSyntax identifier => identifier.Identifier.Text,
            _ => null,
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
    /// Extracts XML documentation elements from the <c>///</c> comment preceding
    /// <paramref name="node"/>, returning a dictionary keyed by tag name. Parameter tags use
    /// keys like <c>param:name</c>; exception tags use <c>exception:TypeName</c>.
    /// </summary>
    private static Dictionary<string, string> ExtractXmlDocumentation(SyntaxNode node)
    {
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var documentationComment = node
            .GetLeadingTrivia()
            .Select(trivia => trivia.GetStructure())
            .OfType<DocumentationCommentTriviaSyntax>()
            .FirstOrDefault();

        if (documentationComment is null)
        {
            return tags;
        }

        foreach (var element in documentationComment.Content.OfType<XmlElementSyntax>())
        {
            var tagName = element.StartTag.Name.LocalName.Text;
            var content = GetXmlElementText(element);
            if (string.IsNullOrEmpty(content))
            {
                continue;
            }

            switch (tagName)
            {
                case "param":
                    var paramName = element.StartTag.Attributes
                        .OfType<XmlNameAttributeSyntax>()
                        .FirstOrDefault()
                        ?.Identifier.Identifier.ValueText;
                    if (!string.IsNullOrEmpty(paramName))
                    {
                        tags[$"param:{paramName}"] = content;
                    }

                    break;

                case "exception":
                    var cref = element.StartTag.Attributes
                        .OfType<XmlCrefAttributeSyntax>()
                        .FirstOrDefault()
                        ?.Cref.ToString();
                    if (!string.IsNullOrEmpty(cref))
                    {
                        tags[$"exception:{cref}"] = content;
                    }

                    break;

                default:
                    tags[tagName] = content;
                    break;
            }
        }

        return tags;
    }

    private static string GetXmlElementText(XmlElementSyntax element)
    {
        var builder = new StringBuilder();
        AppendXmlContentText(element.Content, builder);
        return NormalizeWhitespace(builder.ToString());
    }

    /// <summary>
    /// Renders XML documentation content to plain text, descending into nested formatting
    /// elements (<c>&lt;c&gt;</c>, <c>&lt;para&gt;</c>, …) and substituting reference elements
    /// (<c>&lt;see cref="X"/&gt;</c>, <c>&lt;paramref name="y"/&gt;</c>) with the referenced
    /// name. Skipping these — as a text-only walk would — leaves holes mid-sentence in every
    /// summary shown in the UI ("defined in ." instead of "defined in Theme/*.xaml.").
    /// </summary>
    private static void AppendXmlContentText(SyntaxList<XmlNodeSyntax> content, StringBuilder builder)
    {
        foreach (var node in content)
        {
            switch (node)
            {
                case XmlTextSyntax text:
                    foreach (var token in text.TextTokens)
                    {
                        builder.Append(token.ValueText);
                    }

                    break;

                case XmlElementSyntax nested:
                    AppendXmlContentText(nested.Content, builder);
                    break;

                case XmlEmptyElementSyntax empty:
                    builder.Append(GetXmlReferenceText(empty));
                    break;
            }
        }
    }

    /// <summary>
    /// The display text of a self-closing documentation reference: the cref target for
    /// <c>&lt;see/&gt;</c>/<c>&lt;seealso/&gt;</c>, or the identifier for
    /// <c>&lt;paramref/&gt;</c>/<c>&lt;typeparamref/&gt;</c>.
    /// </summary>
    private static string GetXmlReferenceText(XmlEmptyElementSyntax element)
    {
        foreach (var attribute in element.Attributes)
        {
            switch (attribute)
            {
                case XmlCrefAttributeSyntax cref:
                    return cref.Cref.ToString();
                case XmlNameAttributeSyntax name:
                    return name.Identifier.Identifier.ValueText;
            }
        }

        return string.Empty;
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

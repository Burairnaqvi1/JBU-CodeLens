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
    /// Reads and parses a C# file, returning the names of its top-level classes.
    /// </summary>
    /// <remarks>
    /// Only classes that sit directly inside the compilation unit or a namespace are reported;
    /// classes nested inside other types are intentionally ignored for now. Any I/O or parsing
    /// failure is captured in <see cref="ParseResult.Errors"/> rather than thrown, so callers
    /// can safely run this across many files in a loop.
    /// </remarks>
    /// <param name="filePath">Path to the C# file to parse.</param>
    /// <returns>A <see cref="ParseResult"/> with the discovered class names and any errors.</returns>
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
                result.ClassNames.Add(classDeclaration.Identifier.Text);
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
}

namespace CodeLensAI.Shared.Utilities;

/// <summary>
/// Shared helpers for identifying source files by extension.
/// </summary>
public static class LanguageFileExtensions
{
    private static readonly string[] CppExtensions = { ".cpp", ".hpp", ".h" };

    public static bool IsCSharpFile(string filePath) =>
        string.Equals(Path.GetExtension(filePath), ".cs", StringComparison.OrdinalIgnoreCase);

    public static bool IsCppFile(string filePath) =>
        CppExtensions.Contains(Path.GetExtension(filePath), StringComparer.OrdinalIgnoreCase);
}

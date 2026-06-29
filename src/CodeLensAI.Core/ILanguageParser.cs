namespace CodeLensAI.Core;

/// <summary>
/// Abstraction over a language-specific source parser. Each implementation knows how to
/// read a single source file and report the type declarations it contains, allowing the
/// rest of the application to stay language-agnostic.
/// </summary>
public interface ILanguageParser
{
    /// <summary>
    /// Parses the source file at <paramref name="filePath"/> and returns the discovered
    /// type information. Implementations should not throw for malformed input; instead they
    /// record the problem in <see cref="ParseResult.Errors"/> so a single bad file never
    /// aborts a larger scan.
    /// </summary>
    /// <param name="filePath">Absolute or relative path to the source file to parse.</param>
    /// <returns>A <see cref="ParseResult"/> describing the file's classes and any errors.</returns>
    ParseResult Parse(string filePath);
}

/// <summary>
/// The outcome of parsing a single source file: which file was parsed, the top-level class
/// names that were found, and any errors encountered while reading or parsing it.
/// </summary>
public class ParseResult
{
    /// <summary>
    /// The path of the source file this result describes.
    /// </summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// Names of the top-level classes discovered in the file. Empty when the file has no
    /// classes or could not be parsed.
    /// </summary>
    public List<string> ClassNames { get; set; } = new();

    /// <summary>
    /// Human-readable messages describing any failures (for example, I/O errors). Empty
    /// when parsing succeeded without issues.
    /// </summary>
    public List<string> Errors { get; set; } = new();
}

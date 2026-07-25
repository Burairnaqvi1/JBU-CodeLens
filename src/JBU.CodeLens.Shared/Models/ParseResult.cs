namespace JBU.CodeLens.Shared.Models;

/// <summary>
/// The outcome of parsing a single source file: which file was parsed, the top-level classes
/// that were found (with their members), and any errors encountered while reading or parsing it.
/// </summary>
public class ParseResult
{
    /// <summary>
    /// The path of the source file this result describes.
    /// </summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// The top-level classes discovered in the file, each including its methods and properties.
    /// Empty when the file has no classes or could not be parsed.
    /// </summary>
    public List<ClassInfo> Classes { get; set; } = new();

    /// <summary>
    /// Human-readable messages describing any failures (for example, I/O errors). Empty
    /// when parsing succeeded without issues.
    /// </summary>
    public List<string> Errors { get; set; } = new();
}

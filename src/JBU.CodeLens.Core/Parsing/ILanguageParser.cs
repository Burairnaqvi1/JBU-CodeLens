namespace JBU.CodeLens.Core.Parsing;

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

    /// <summary>
    /// Asynchronous variant of <see cref="Parse"/>: the file read is awaited
    /// (<see cref="File.ReadAllTextAsync(string, CancellationToken)"/>) while the CPU-bound
    /// parse itself runs synchronously on the calling (worker) thread. Same error contract
    /// as <see cref="Parse"/>.
    /// </summary>
    Task<ParseResult> ParseAsync(string filePath, CancellationToken cancellationToken = default);
}

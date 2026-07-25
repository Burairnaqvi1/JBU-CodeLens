namespace JBU.CodeLens.Shared.Models;

/// <summary>
/// Progress snapshot for a project scan: how many files have finished parsing out of the total,
/// and the file that most recently completed.
/// </summary>
public readonly record struct ScanProgress(int FilesParsed, int TotalFiles, string CurrentFile);

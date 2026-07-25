namespace JBU.CodeLens.Shared.Models;

/// <summary>
/// SCIDE project metrics passed into export without coupling Core to SCIDE assemblies.
/// </summary>
public sealed class ProjectMetricsSnapshot
{
    public int TotalClasses { get; init; }
    public int TotalMethods { get; init; }
    public int TotalNamespaces { get; init; }
    public int TotalRelationships { get; init; }
    public double MaintainabilityIndex { get; init; }
    public double AverageComplexity { get; init; }
    public int MaxComplexity { get; init; }
}

namespace ContextManager.Mcp.Serialization;

/// <summary>
/// Result returned by <c>graph_impact_analysis</c>: the set of affected node IDs and
/// <c>AffectedFiles</c> mapping each returned affected ID to its declaring source file.
/// </summary>
public sealed record GraphImpactResult(
    IReadOnlyList<string> AffectedIds,
    IReadOnlyDictionary<string, string>? AffectedFiles = null);

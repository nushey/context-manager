namespace ContextManager.Mcp.Serialization;

/// <summary>
/// Result returned by <c>graph_impact_analysis</c>: the set of affected node IDs
/// plus a diagnostics block that flags probable reflection blind spots.
/// </summary>
public sealed record GraphImpactResult(
    IReadOnlyList<string> AffectedIds,
    IReadOnlyList<GraphImpactDiagnostic> Diagnostics);

/// <summary>
/// A single diagnostic entry emitted when an interface in the analyzed scope
/// has no in-graph implementations, indicating it may be wired via reflection
/// or dynamic dispatch and the impact set could be incomplete.
/// </summary>
public sealed record GraphImpactDiagnostic(
    string Code,
    string Message,
    string InterfaceId);

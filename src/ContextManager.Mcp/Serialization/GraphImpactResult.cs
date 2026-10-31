namespace ContextManager.Mcp.Serialization;

public sealed record GraphImpactResult(
    IReadOnlyList<string> AffectedIds,
    IReadOnlyDictionary<string, string>? AffectedFiles = null);

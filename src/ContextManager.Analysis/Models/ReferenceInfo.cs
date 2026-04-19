namespace ContextManager.Analysis.Models;

public sealed record ReferenceInfo(
    string From,
    string To,
    string Via,
    string? ResolvedFile);

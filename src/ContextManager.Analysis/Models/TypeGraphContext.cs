namespace ContextManager.Analysis.Models;

public sealed record TypeGraphContext(
    string NodeId,
    int InboundCount,
    int OutboundCount,
    IReadOnlyList<GraphEndpoint>? DirectCallers = null,
    IReadOnlyList<GraphEndpoint>? DirectDependencies = null);

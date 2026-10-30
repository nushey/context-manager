namespace ContextManager.Analysis.Graph;

public sealed class GraphSyncException(string phase, Exception innerException)
    : Exception($"Graph synchronization failed during {phase}.", innerException)
{
    public string Phase { get; } = phase;
}

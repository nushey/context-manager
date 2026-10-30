namespace ContextManager.Analysis.Graph;

public interface IGraphSynchronizer
{
    /// <summary>
    /// Brings the solution's graph up to date and returns the resulting snapshot. Builds the graph
    /// when graph.json is missing, and otherwise re-extracts only the invalidated contributions.
    /// <paramref name="reevaluate"/> forces a fresh MSBuild evaluation instead of trusting the
    /// on-disk freshness check. Returns null when neither graph.json nor the .sln exists.
    /// Failures surface as <see cref="GraphSyncException"/>; cancellation as
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    Task<GraphSyncResult?> SynchronizeAsync(string solutionPath, bool reevaluate, CancellationToken ct = default);
}

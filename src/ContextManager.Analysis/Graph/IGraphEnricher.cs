using ContextManager.Analysis.Models;

namespace ContextManager.Analysis.Graph;

public interface IGraphEnricher
{
    /// <summary>
    /// Attaches the graph context of each type of the file at <paramref name="filePath"/> found in its
    /// enclosing solution graph, or a
    /// <c>GraphStatus</c> explaining why no graph data is attached. Never builds or synchronizes a
    /// graph, so the attached context reflects the last persisted graph.
    /// </summary>
    FileAnalysis Enrich(FileAnalysis analysis, string filePath);

    /// <summary>
    /// Adds graph hints for <c>Unresolved</c> type names, looked up in the graph of each input
    /// file's enclosing solution. Every short-name match is returned; a hint is not an
    /// authoritative resolution.
    /// </summary>
    ContextAnalysis Enrich(ContextAnalysis analysis);
}

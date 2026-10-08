using System.Collections.Concurrent;

namespace ContextManager.Analysis.Graph;

public class GraphRegistry
{
    private sealed class Entry
    {
        public readonly GraphStore Store = new();
        public readonly object Gate = new();
        public DateTime LoadedWriteTimeUtc;
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public static string GetGraphPath(string solutionPath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(solutionPath)) ?? string.Empty, ".context-manager", "graph.json");

    public GraphStore GetStore(string solutionPath) => GetEntry(solutionPath).Store;

    public GraphStore? TryLoad(string solutionPath)
    {
        var graphPath = GetGraphPath(solutionPath);
        if (!File.Exists(graphPath))
            return null;

        var entry = GetEntry(solutionPath);
        var writeTimeUtc = File.GetLastWriteTimeUtc(graphPath);
        lock (entry.Gate)
        {
            if (entry.LoadedWriteTimeUtc != writeTimeUtc)
            {
                entry.Store.Deserialize(File.ReadAllText(graphPath));
                entry.LoadedWriteTimeUtc = writeTimeUtc;
            }
        }

        return entry.Store;
    }

    private Entry GetEntry(string solutionPath) =>
        _entries.GetOrAdd(Path.GetFullPath(solutionPath), _ => new Entry());
}

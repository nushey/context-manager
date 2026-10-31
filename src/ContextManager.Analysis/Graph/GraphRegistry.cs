using System.Collections.Concurrent;

namespace ContextManager.Analysis.Graph;

public class GraphRegistry
{
    private sealed class Entry
    {
        public readonly GraphStore Store = new();
        public readonly object Gate = new();
        public readonly SemaphoreSlim Writer = new(1, 1);
        public DateTime LoadedWriteTimeUtc;
    }

    private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        public void Dispose() => semaphore.Release();
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public static string GetGraphPath(string solutionPath)
    {
        var fullPath = Path.GetFullPath(solutionPath);
        return Path.Combine(
            Path.GetDirectoryName(fullPath) ?? string.Empty,
            ".context-manager",
            $"{Path.GetFileNameWithoutExtension(fullPath)}.graph.json");
    }

    public GraphStore? TryLoad(string solutionPath)
    {
        var graphPath = Path.GetFullPath(GetGraphPath(solutionPath));
        if (!File.Exists(graphPath))
            return null;

        var entry = GetEntry(graphPath);
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

    internal async Task<IDisposable> AcquireWriterAsync(string solutionPath, CancellationToken ct)
    {
        var entry = GetEntry(GetGraphPath(solutionPath));
        await entry.Writer.WaitAsync(ct);
        return new Releaser(entry.Writer);
    }

    internal async Task SaveAsync(string solutionPath, GraphStore candidate, CancellationToken ct)
    {
        var graphPath = GetGraphPath(solutionPath);
        await WriteAtomicallyAsync(graphPath, candidate.Serialize(), ct);

        var entry = GetEntry(graphPath);
        lock (entry.Gate)
        {
            entry.Store.Publish(candidate);
            entry.LoadedWriteTimeUtc = File.GetLastWriteTimeUtc(graphPath);
        }
    }

    public static async Task WriteAtomicallyAsync(string path, string content, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await File.WriteAllTextAsync(temporaryPath, content, ct);
            if (File.Exists(path))
                File.Replace(temporaryPath, path, null);
            else
                File.Move(temporaryPath, path);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private Entry GetEntry(string graphPath) =>
        _entries.GetOrAdd(Path.GetFullPath(graphPath), _ => new Entry());
}

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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

    internal readonly record struct GraphFileVersion(bool Exists, string? Revision, DateTime WriteTimeUtc);

    private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        public void Dispose() => semaphore.Release();
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public static string GetGraphPath(string solutionPath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(solutionPath)) ?? string.Empty, ".context-manager", "graph.json");

    public GraphStore GetStore(string solutionPath) => GetEntry(GetGraphPath(solutionPath)).Store;

    public GraphStore? TryLoad(string solutionPath) => TryLoadGraph(GetGraphPath(solutionPath));

    public GraphStore? TryLoadGraph(string graphPath)
    {
        graphPath = Path.GetFullPath(graphPath);
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

    /// <summary>
    /// Resolves the graph that covers <paramref name="pathOrFile"/> (a .sln, a source file, or a
    /// directory) by walking up from its directory to the first directory that holds a
    /// <c>.context-manager/graph.json</c> or at least one <c>.sln</c> file. Returns null when no
    /// such directory exists; several solutions in that directory are all returned so the caller
    /// never picks one arbitrarily.
    /// </summary>
    public GraphLocation? TryResolveSolutionPath(string pathOrFile)
    {
        var fullPath = Path.GetFullPath(pathOrFile);
        if (fullPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
            return new GraphLocation(GetGraphPath(fullPath), [fullPath]);

        var directory = Directory.Exists(fullPath) ? new DirectoryInfo(fullPath) : new FileInfo(fullPath).Directory;
        for (; directory is not null; directory = directory.Parent)
        {
            if (!directory.Exists)
                continue;

            var graphPath = Path.Combine(directory.FullName, ".context-manager", "graph.json");
            var solutions = directory.GetFiles("*.sln").Select(f => f.FullName).ToList();
            if (solutions.Count > 0 || File.Exists(graphPath))
                return new GraphLocation(graphPath, solutions);
        }

        return null;
    }

    internal async Task<IDisposable> AcquireWriterAsync(string solutionPath, CancellationToken ct)
    {
        var entry = GetEntry(GetGraphPath(solutionPath));
        await entry.Writer.WaitAsync(ct);
        return new Releaser(entry.Writer);
    }

    internal static GraphFileVersion ReadVersion(string graphPath)
    {
        if (!File.Exists(graphPath))
            return new GraphFileVersion(false, null, default);

        var writeTimeUtc = File.GetLastWriteTimeUtc(graphPath);
        using var stream = new FileStream(graphPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new byte[256];
        var length = stream.Read(buffer, 0, buffer.Length);
        var reader = new Utf8JsonReader(buffer.AsSpan(0, length), isFinalBlock: false, state: default);
        string? revision = null;
        if (reader.Read() && reader.TokenType == JsonTokenType.StartObject &&
            reader.Read() && reader.TokenType == JsonTokenType.PropertyName && reader.ValueTextEquals("revision") &&
            reader.Read() && reader.TokenType == JsonTokenType.String)
        {
            revision = reader.GetString();
        }

        return new GraphFileVersion(true, revision, writeTimeUtc);
    }

    internal async Task<bool> SaveAsync(
        string solutionPath,
        GraphStore candidate,
        GraphFileVersion expected,
        Func<string, string, CancellationToken, Task> writeFile,
        CancellationToken ct)
    {
        var graphPath = GetGraphPath(solutionPath);
        var json = candidate.Serialize();
        using (await AcquireFileLockAsync(graphPath, ct))
        {
            if (ReadVersion(graphPath) != expected)
                return false;

            await writeFile(graphPath, json, ct);

            var entry = GetEntry(graphPath);
            lock (entry.Gate)
            {
                entry.Store.Publish(candidate);
                if (File.Exists(graphPath))
                    entry.LoadedWriteTimeUtc = File.GetLastWriteTimeUtc(graphPath);
            }
        }

        return true;
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

    private static async Task<IDisposable> AcquireFileLockAsync(string graphPath, CancellationToken ct)
    {
        var lockDirectory = Path.Combine(Path.GetTempPath(), "context-manager-locks");
        Directory.CreateDirectory(lockDirectory);
        var lockPath = Path.Combine(lockDirectory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(graphPath))) + ".lock");

        while (true)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                await Task.Delay(50, ct);
            }
        }
    }

    private Entry GetEntry(string graphPath) =>
        _entries.GetOrAdd(Path.GetFullPath(graphPath), _ => new Entry());
}

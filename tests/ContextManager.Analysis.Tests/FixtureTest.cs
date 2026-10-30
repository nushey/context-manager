using ContextManager.Analysis.Graph;
using ContextManager.Mcp.Tools;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ContextManager.Analysis.Tests;

public abstract class FixtureTest
{
    protected static readonly Action EnsureMsBuild = () =>
        typeof(ProjectScanTool).Assembly.GetType("ContextManager.Mcp.MsBuildBootstrap")!
            .GetMethod("EnsureRegistered")!.Invoke(null, null);

    private readonly List<string> _directories = [];
    private readonly List<IDisposable> _disposables = [];

    [TestCleanup]
    public void DeleteFixtureState()
    {
        foreach (var disposable in _disposables)
            disposable.Dispose();

        foreach (var directory in _directories)
            Directory.Delete(directory, true);
    }

    protected string CopyFixture(params string[] relative) => Track(FixtureCopy.CopyToTemp(FixtureCopy.SourcePath(relative)));

    protected string CopyDirectory(string source) => Track(FixtureCopy.CopyToTemp(source));

    protected string NewTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"context-manager-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return Track(directory);
    }

    protected T Own<T>(T disposable) where T : IDisposable
    {
        _disposables.Add(disposable);
        return disposable;
    }

    protected GraphSynchronizer CreateSynchronizer(GraphRegistry registry, Func<string, string, CancellationToken, Task>? writeFile = null) =>
        Own(new GraphSynchronizer(new GraphBuilder(new EdgeExtractor()), registry, EnsureMsBuild, writeFile ?? GraphRegistry.WriteAtomicallyAsync));

    private string Track(string directory)
    {
        _directories.Add(directory);
        return directory;
    }
}

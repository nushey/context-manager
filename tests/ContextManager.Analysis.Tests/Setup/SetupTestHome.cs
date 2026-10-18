using ContextManager.Mcp.Setup;

namespace ContextManager.Analysis.Tests.Setup;

internal sealed class SetupTestHome : IDisposable
{
    public SetupTestHome(SetupPlatform platform = SetupPlatform.Linux, string? xdgConfigHome = null)
    {
        Root = Path.Combine(Path.GetTempPath(), $"context-manager-setup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Root);
        Environment = new SetupEnvironment(platform, Path.Combine(Root, "home"), Path.Combine(Root, "appdata"), xdgConfigHome);
    }

    public string Root { get; }

    public SetupEnvironment Environment { get; }

    public SetupClient Client(string id) => SetupCatalog.For(Environment).Single(client => client.Id == id);

    public SetupClient ClientWithFixture(string id, string fixture)
    {
        var client = Client(id);
        Directory.CreateDirectory(Path.GetDirectoryName(client.ConfigPath)!);
        File.Copy(FixturePath(fixture), client.ConfigPath);
        return client;
    }

    public static string FixturePath(string name) => Path.Combine(
        Path.GetDirectoryName(typeof(SetupTestHome).Assembly.Location)!,
        "Fixtures",
        "SetupFixtures",
        name);

    public void Dispose() => Directory.Delete(Root, recursive: true);
}

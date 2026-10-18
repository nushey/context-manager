using ContextManager.Mcp.Setup;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ContextManager.Analysis.Tests.Setup;

[TestClass]
public class TomlConfigAppenderTests
{
    private const string CodexTable = "[mcp_servers.context-manager]\ncommand = \"context-manager\"\nargs = []\n";

    private readonly TomlConfigAppender _appender = new();

    [TestMethod]
    public void Apply_MissingFile_CreatesFileWithTable()
    {
        using var home = new SetupTestHome();
        var client = home.Client("codex");

        var result = _appender.Apply(client);

        Assert.AreEqual(SetupOutcome.Configured, result.Outcome, result.Error);
        Assert.AreEqual(CodexTable, File.ReadAllText(client.ConfigPath));
        Assert.IsTrue(_appender.IsConfigured(client));
    }

    [TestMethod]
    public void Apply_ExistingTables_AppendsTableAndKeepsOriginalContent()
    {
        using var home = new SetupTestHome();
        var client = home.ClientWithFixture("codex", "codex-tables.toml");
        var before = File.ReadAllText(client.ConfigPath);

        Assert.IsFalse(_appender.IsConfigured(client));
        var result = _appender.Apply(client);

        Assert.AreEqual(SetupOutcome.Configured, result.Outcome, result.Error);
        Assert.AreEqual(before + "\n" + CodexTable, File.ReadAllText(client.ConfigPath));
    }

    [TestMethod]
    [DataRow("codex-configured-table.toml")]
    [DataRow("codex-configured-dotted.toml")]
    [DataRow("codex-configured-env-subtable.toml")]
    public void Apply_AlreadyConfigured_SkipsAndLeavesFileUntouched(string fixture)
    {
        using var home = new SetupTestHome();
        var client = home.ClientWithFixture("codex", fixture);
        var before = File.ReadAllBytes(client.ConfigPath);

        Assert.IsTrue(_appender.IsConfigured(client));
        var result = _appender.Apply(client);

        Assert.AreEqual(SetupOutcome.Skipped, result.Outcome);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(client.ConfigPath));
    }
}

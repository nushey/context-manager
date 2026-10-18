using ContextManager.Mcp.Setup;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Spectre.Console;

namespace ContextManager.Analysis.Tests.Setup;

[TestClass]
public class SetupCommandTests
{
    private static SetupCommand CreateCommand(SetupTestHome home) => new(
        home.Environment,
        AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(new StringWriter()) }),
        new JsonConfigInserter(),
        new TomlConfigAppender());

    [TestMethod]
    public void DescribeChoice_ParentDirectoryExists_MarksDetected()
    {
        using var home = new SetupTestHome();
        Directory.CreateDirectory(Path.Combine(home.Environment.Home, ".cursor"));
        var command = CreateCommand(home);

        Assert.AreEqual("Cursor [grey](detected)[/]", command.DescribeChoice(home.Client("cursor")));
        Assert.AreEqual("Codex (CLI · IDE · ChatGPT desktop)", command.DescribeChoice(home.Client("codex")));
    }

    [TestMethod]
    public void DescribeChoice_ClaudeCode_DetectedOnlyWhenClaudeJsonExists()
    {
        using var home = new SetupTestHome();
        Directory.CreateDirectory(home.Environment.Home);
        var command = CreateCommand(home);

        Assert.AreEqual("Claude Code", command.DescribeChoice(home.Client("claude-code")));

        home.ClientWithFixture("claude-code", "claude-nested-projects.json");

        Assert.AreEqual("Claude Code [grey](detected)[/]", command.DescribeChoice(home.Client("claude-code")));
    }

    [TestMethod]
    public void DescribeChoice_ExistingEntry_MarksAlreadyConfigured()
    {
        using var home = new SetupTestHome();
        var cursor = home.ClientWithFixture("cursor", "already-configured.json");
        var codex = home.ClientWithFixture("codex", "codex-configured-dotted.toml");
        var command = CreateCommand(home);

        Assert.AreEqual("Cursor [grey](detected)[/] [grey](already configured)[/]", command.DescribeChoice(cursor));
        Assert.AreEqual("Codex (CLI · IDE · ChatGPT desktop) [grey](detected)[/] [grey](already configured)[/]", command.DescribeChoice(codex));
    }
}

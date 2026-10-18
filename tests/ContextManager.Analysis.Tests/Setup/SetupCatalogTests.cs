using ContextManager.Mcp.Setup;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ContextManager.Analysis.Tests.Setup;

[TestClass]
public class SetupCatalogTests
{
    [TestMethod]
    public void For_Linux_ExcludesClaudeDesktop()
    {
        var environment = new SetupEnvironment(SetupPlatform.Linux, "/home/user", "/home/user/.config", null);

        var ids = SetupCatalog.For(environment).Select(client => client.Id).ToList();

        CollectionAssert.AreEqual(
            new[] { "claude-code", "codex", "antigravity", "cursor", "devin", "kilo", "opencode" },
            ids);
    }

    [TestMethod]
    public void For_MacOS_IncludesClaudeDesktopUnderApplicationSupport()
    {
        var environment = new SetupEnvironment(SetupPlatform.MacOS, "/Users/user", "/Users/user/.config", null);

        var client = SetupCatalog.For(environment).Single(c => c.Id == "claude-desktop");

        Assert.AreEqual(Path.Combine("/Users/user", "Library", "Application Support", "Claude", "claude_desktop_config.json"), client.ConfigPath);
        Assert.AreEqual(8, SetupCatalog.For(environment).Count);
    }

    [TestMethod]
    public void For_Windows_UsesAppDataForClaudeDesktopAndDevin()
    {
        var environment = new SetupEnvironment(SetupPlatform.Windows, "C:/Users/user", "C:/Users/user/AppData/Roaming", "/ignored/xdg");

        var clients = SetupCatalog.For(environment);

        Assert.AreEqual(Path.Combine("C:/Users/user/AppData/Roaming", "Claude", "claude_desktop_config.json"), clients.Single(c => c.Id == "claude-desktop").ConfigPath);
        Assert.AreEqual(Path.Combine("C:/Users/user/AppData/Roaming", "devin", "mcp_config.json"), clients.Single(c => c.Id == "devin").ConfigPath);
        Assert.AreEqual(Path.Combine("C:/Users/user", ".config", "kilo", "kilo.jsonc"), clients.Single(c => c.Id == "kilo").ConfigPath);
    }

    [TestMethod]
    public void For_DevinWithXdgConfigHome_UsesXdgConfigHome()
    {
        var environment = new SetupEnvironment(SetupPlatform.Linux, "/home/user", "/home/user/.config", "/custom/xdg");

        var client = SetupCatalog.For(environment).Single(c => c.Id == "devin");

        Assert.AreEqual(Path.Combine("/custom/xdg", "devin", "mcp_config.json"), client.ConfigPath);
    }

    [TestMethod]
    public void For_DevinWithoutXdgConfigHome_UsesHomeConfig()
    {
        var environment = new SetupEnvironment(SetupPlatform.Linux, "/home/user", "/home/user/.config", null);

        var client = SetupCatalog.For(environment).Single(c => c.Id == "devin");

        Assert.AreEqual(Path.Combine("/home/user", ".config", "devin", "mcp_config.json"), client.ConfigPath);
    }

    [TestMethod]
    public void For_DevinWithEmptyXdgConfigHome_UsesHomeConfig()
    {
        var environment = new SetupEnvironment(SetupPlatform.Linux, "/home/user", "/home/user/.config", "");

        var client = SetupCatalog.For(environment).Single(c => c.Id == "devin");

        Assert.AreEqual(Path.Combine("/home/user", ".config", "devin", "mcp_config.json"), client.ConfigPath);
    }
}

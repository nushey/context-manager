using System.Text.Json;
using ContextManager.Mcp.Setup;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ContextManager.Analysis.Tests.Setup;

[TestClass]
public class JsonConfigInserterTests
{
    private const string StdioEntry = "\"context-manager\": { \"command\": \"context-manager\" }";

    private readonly JsonConfigInserter _inserter = new();

    [TestMethod]
    public void Apply_EmptyServersObject_InsertsEntryAsOnlyMember()
    {
        using var home = new SetupTestHome();
        var client = home.ClientWithFixture("cursor", "empty-servers.json");

        var result = _inserter.Apply(client);

        Assert.AreEqual(SetupOutcome.Configured, result.Outcome, result.Error);
        Assert.AreEqual($"{{\n  \"mcpServers\": {{ {StdioEntry} }}\n}}\n", File.ReadAllText(client.ConfigPath));
    }

    [TestMethod]
    public void Apply_ServersWithMembers_InsertsEntryFirstAndKeepsExistingMembers()
    {
        using var home = new SetupTestHome();
        var client = home.ClientWithFixture("cursor", "with-members.json");

        var result = _inserter.Apply(client);

        Assert.AreEqual(SetupOutcome.Configured, result.Outcome, result.Error);
        var expected = $"{{\n  \"mcpServers\": {{ {StdioEntry},\n    \"other-server\": {{\n      \"command\": \"other\"\n    }}\n  }}\n}}\n";
        Assert.AreEqual(expected, File.ReadAllText(client.ConfigPath));
    }

    [TestMethod]
    public void Apply_FileWithUtf8Bom_InsertsEntryAndKeepsBom()
    {
        using var home = new SetupTestHome();
        var client = home.ClientWithFixture("cursor", "with-bom.json");

        var result = _inserter.Apply(client);

        Assert.AreEqual(SetupOutcome.Configured, result.Outcome, result.Error);
        var bytes = File.ReadAllBytes(client.ConfigPath);
        CollectionAssert.AreEqual(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        var expected = $"{{\n  \"mcpServers\": {{ {StdioEntry},\n    \"other-server\": {{\n      \"command\": \"other\"\n    }}\n  }}\n}}\n";
        Assert.AreEqual(expected, System.Text.Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));
    }

    [TestMethod]
    public void Apply_MissingServersKey_InsertsKeyAfterRootBrace()
    {
        using var home = new SetupTestHome();
        var client = home.ClientWithFixture("antigravity", "missing-key.json");

        var result = _inserter.Apply(client);

        Assert.AreEqual(SetupOutcome.Configured, result.Outcome, result.Error);
        Assert.AreEqual($"{{ \"mcpServers\": {{ {StdioEntry} }},\n  \"theme\": \"dark\"\n}}\n", File.ReadAllText(client.ConfigPath));
    }

    [TestMethod]
    public void Apply_MissingFile_CreatesDirectoryAndDocumentedSnippet()
    {
        using var home = new SetupTestHome();
        var client = home.Client("cursor");

        var result = _inserter.Apply(client);

        Assert.AreEqual(SetupOutcome.Configured, result.Outcome, result.Error);
        using var document = JsonDocument.Parse(File.ReadAllText(client.ConfigPath));
        var entry = document.RootElement.GetProperty("mcpServers").GetProperty("context-manager");
        Assert.AreEqual("context-manager", entry.GetProperty("command").GetString());
    }

    [TestMethod]
    public void Apply_EmptyFile_WritesDocumentedSnippet()
    {
        using var home = new SetupTestHome();
        var client = home.ClientWithFixture("cursor", "empty-file.json");

        var result = _inserter.Apply(client);

        Assert.AreEqual(SetupOutcome.Configured, result.Outcome, result.Error);
        using var document = JsonDocument.Parse(File.ReadAllText(client.ConfigPath));
        var entry = document.RootElement.GetProperty("mcpServers").GetProperty("context-manager");
        Assert.AreEqual("context-manager", entry.GetProperty("command").GetString());
    }

    [TestMethod]
    public void Apply_MissingOpencodeFile_CreatesSnippetWithSchema()
    {
        using var home = new SetupTestHome();
        var client = home.Client("opencode");

        var result = _inserter.Apply(client);

        Assert.AreEqual(SetupOutcome.Configured, result.Outcome, result.Error);
        using var document = JsonDocument.Parse(File.ReadAllText(client.ConfigPath));
        Assert.AreEqual("https://opencode.ai/config.json", document.RootElement.GetProperty("$schema").GetString());
        var entry = document.RootElement.GetProperty("mcp").GetProperty("context-manager");
        Assert.AreEqual("local", entry.GetProperty("type").GetString());
        Assert.AreEqual("context-manager", entry.GetProperty("command")[0].GetString());
        Assert.IsTrue(entry.GetProperty("enabled").GetBoolean());
    }

    [TestMethod]
    public void Apply_CommentsAndTrailingCommas_KeepsFileByteIdenticalExceptInsertion()
    {
        using var home = new SetupTestHome();
        var client = home.ClientWithFixture("kilo", "kilo-comments.jsonc");

        var result = _inserter.Apply(client);

        Assert.AreEqual(SetupOutcome.Configured, result.Outcome, result.Error);
        CollectionAssert.AreEqual(
            File.ReadAllBytes(SetupTestHome.FixturePath("kilo-comments.expected.jsonc")),
            File.ReadAllBytes(client.ConfigPath));
    }

    [TestMethod]
    public void Apply_ClaudeJsonWithNestedProjectServers_InsertsAtRootOnly()
    {
        using var home = new SetupTestHome();
        var client = home.ClientWithFixture("claude-code", "claude-nested-projects.json");

        Assert.IsFalse(_inserter.IsConfigured(client));
        var result = _inserter.Apply(client);

        Assert.AreEqual(SetupOutcome.Configured, result.Outcome, result.Error);
        using var document = JsonDocument.Parse(File.ReadAllText(client.ConfigPath));
        var root = document.RootElement;
        Assert.AreEqual("context-manager", root.GetProperty("mcpServers").GetProperty("context-manager").GetProperty("command").GetString());
        var nestedServers = root.GetProperty("projects").GetProperty("/home/user/repo").GetProperty("mcpServers");
        Assert.AreEqual(1, nestedServers.EnumerateObject().Count());
        Assert.AreEqual(12, root.GetProperty("numStartups").GetInt32());
        Assert.IsTrue(_inserter.IsConfigured(client));
    }

    [TestMethod]
    public void Apply_AlreadyConfigured_SkipsAndLeavesFileUntouched()
    {
        using var home = new SetupTestHome();
        var client = home.ClientWithFixture("cursor", "already-configured.json");
        var before = File.ReadAllBytes(client.ConfigPath);

        Assert.IsTrue(_inserter.IsConfigured(client));
        var result = _inserter.Apply(client);

        Assert.AreEqual(SetupOutcome.Skipped, result.Outcome);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(client.ConfigPath));
    }

    [TestMethod]
    public void Apply_InvalidJson_FailsAndLeavesFileUntouched()
    {
        using var home = new SetupTestHome();
        var client = home.ClientWithFixture("cursor", "invalid.json");
        var before = File.ReadAllBytes(client.ConfigPath);

        Assert.IsFalse(_inserter.IsConfigured(client));
        var result = _inserter.Apply(client);

        Assert.AreEqual(SetupOutcome.Failed, result.Outcome);
        Assert.IsFalse(string.IsNullOrEmpty(result.Error));
        CollectionAssert.AreEqual(before, File.ReadAllBytes(client.ConfigPath));
    }
}

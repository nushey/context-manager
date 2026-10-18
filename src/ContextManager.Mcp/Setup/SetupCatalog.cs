namespace ContextManager.Mcp.Setup;

public static class SetupCatalog
{
    public const string ServerName = "context-manager";

    private const string McpServersKey = "mcpServers";
    private const string McpKey = "mcp";
    private const string StdioEntry = """{ "command": "context-manager" }""";
    private const string LocalEntry = """{ "type": "local", "command": ["context-manager"], "enabled": true }""";
    private const string CodexEntry = "command = \"context-manager\"\nargs = []";

    private const string McpServersFile = """
        {
          "mcpServers": {
            "context-manager": {
              "command": "context-manager"
            }
          }
        }

        """;

    private const string KiloFile = """
        {
          "mcp": {
            "context-manager": {
              "type": "local",
              "command": ["context-manager"],
              "enabled": true
            }
          }
        }

        """;

    private const string OpencodeFile = """
        {
          "$schema": "https://opencode.ai/config.json",
          "mcp": {
            "context-manager": {
              "type": "local",
              "command": ["context-manager"],
              "enabled": true
            }
          }
        }

        """;

    public static IReadOnlyList<SetupClient> For(SetupEnvironment environment)
    {
        var home = environment.Home;
        var isWindows = environment.Platform == SetupPlatform.Windows;
        var claudeCodePath = Path.Combine(home, ".claude.json");
        var clients = new List<SetupClient>
        {
            Json("claude-code", "Claude Code", claudeCodePath, McpServersKey, StdioEntry, McpServersFile) with { DetectionPath = claudeCodePath }
        };

        if (environment.Platform != SetupPlatform.Linux)
        {
            var claudeDesktopPath = isWindows
                ? Path.Combine(environment.AppData, "Claude", "claude_desktop_config.json")
                : Path.Combine(home, "Library", "Application Support", "Claude", "claude_desktop_config.json");
            clients.Add(Json("claude-desktop", "Claude Desktop", claudeDesktopPath, McpServersKey, StdioEntry, McpServersFile));
        }

        var codexPath = Path.Combine(home, ".codex", "config.toml");
        clients.Add(new SetupClient(
            "codex",
            "Codex (CLI · IDE · ChatGPT desktop)",
            codexPath,
            Path.GetDirectoryName(codexPath)!,
            ConfigFormat.Toml,
            "mcp_servers",
            CodexEntry,
            null));

        clients.Add(Json("antigravity", "Antigravity (IDE · CLI)", Path.Combine(home, ".gemini", "config", "mcp_config.json"), McpServersKey, StdioEntry, McpServersFile));
        clients.Add(Json("cursor", "Cursor", Path.Combine(home, ".cursor", "mcp.json"), McpServersKey, StdioEntry, McpServersFile));

        var devinRoot = isWindows
            ? environment.AppData
            : string.IsNullOrEmpty(environment.XdgConfigHome) ? Path.Combine(home, ".config") : environment.XdgConfigHome;
        clients.Add(Json("devin", "Devin (Desktop · CLI)", Path.Combine(devinRoot, "devin", "mcp_config.json"), McpServersKey, StdioEntry, McpServersFile));

        clients.Add(Json("kilo", "Kilo Code", Path.Combine(home, ".config", "kilo", "kilo.jsonc"), McpKey, LocalEntry, KiloFile));
        clients.Add(Json("opencode", "Opencode", Path.Combine(home, ".config", "opencode", "opencode.json"), McpKey, LocalEntry, OpencodeFile));

        return clients;
    }

    private static SetupClient Json(string id, string displayName, string configPath, string serversKey, string entry, string newFileContent) =>
        new(id, displayName, configPath, Path.GetDirectoryName(configPath)!, ConfigFormat.Json, serversKey, entry, newFileContent);
}

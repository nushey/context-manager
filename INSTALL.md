# Installing ContextManager

ContextManager ships as a .NET global tool. Every MCP client starts the same `context-manager`
executable over stdio.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10) or later (`dotnet --version`).
- Only for `project_scan` on `.NET Framework 4.8` solutions, on Windows:
  [Visual Studio Build Tools 2022/2026](https://visualstudio.microsoft.com/downloads/) with the
  **.NET desktop build tools** workload. ContextManager uses the MSBuild of the newest installed
  Visual Studio or Build Tools, so a Visual Studio update can require a newer ContextManager.

## Quick install

**macOS / Linux:**

```bash
curl -fsSL https://raw.githubusercontent.com/nushey/ContextManager/main/scripts/install.sh | bash
```

**Windows (PowerShell):**

```powershell
irm https://raw.githubusercontent.com/nushey/ContextManager/main/scripts/install.ps1 | iex
```

The installer checks for the .NET 10 SDK, installs or updates the tool, and runs
`context-manager setup`. The wizard lists the clients supported on your OS and configures the
ones you select.

To configure every supported client without prompts, add `-y`:

```bash
curl -fsSL https://raw.githubusercontent.com/nushey/ContextManager/main/scripts/install.sh | bash -s -- -y
```

```powershell
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/nushey/ContextManager/main/scripts/install.ps1))) -Yes
```

You can run the wizard again at any time with `context-manager setup` (or `context-manager setup -y`).
It only edits user-level config files:

- It adds the `context-manager` entry and leaves the rest of the file as it was, comments included.
- It skips clients that already have the entry, so running it twice is safe.
- It does not touch files it cannot parse; it reports them as `failed`.
- Without a terminal you must pass `-y`; otherwise it exits with code 1.

## Manual install

```bash
dotnet tool install -g ContextManager
context-manager --version
```

Update it later with `dotnet tool update -g ContextManager`.

The tool lands in `~/.dotnet/tools` (Windows: `%USERPROFILE%\.dotnet\tools`). Clients start it
without a login shell, so that directory must be on `PATH`. If a client reports
*command not found*, add it to `PATH` or use the absolute path to the executable as the command,
for example `C:\Users\<you>\.dotnet\tools\context-manager.exe`.

## Client configuration

After editing a config file, quit and reopen the client so it reads the new configuration.

### Claude Code

```bash
claude mcp add --scope user context-manager -- context-manager
```

Without `--scope user` the server is added to the current project only. If you edit `~/.claude.json`
by hand, use the root-level `mcpServers` key shown below, not the per-project entries.

### Clients with an `mcpServers` block

Claude Code, Claude Desktop, Antigravity, Cursor, and Devin use this block:

```json
{
  "mcpServers": {
    "context-manager": {
      "command": "context-manager"
    }
  }
}
```

| Client | Config file |
|--------|-------------|
| Claude Code | `~/.claude.json` |
| Claude Desktop | Windows: `%APPDATA%\Claude\claude_desktop_config.json`<br>macOS: `~/Library/Application Support/Claude/claude_desktop_config.json`<br>Linux: not supported |
| Antigravity (IDE, CLI, 2.0) | `~/.gemini/config/mcp_config.json` |
| Cursor | `~/.cursor/mcp.json`, or `.cursor/mcp.json` in a repo to override it |
| Devin (Desktop, ex-Windsurf, and CLI) | Windows: `%APPDATA%\devin\mcp_config.json`<br>macOS / Linux: `~/.config/devin/mcp_config.json` |

### Kilo Code and Opencode

Both use an `mcp` key where `command` is an array:

```jsonc
{
  "mcp": {
    "context-manager": {
      "type": "local",
      "command": ["context-manager"],
      "enabled": true
    }
  }
}
```

| Client | Config file |
|--------|-------------|
| Kilo Code | `~/.config/kilo/kilo.jsonc`; a project `kilo.jsonc` or `.kilo/kilo.jsonc` overrides it |
| Opencode | `~/.config/opencode/opencode.json`, or `opencode.json` in the project |

### Codex

Codex CLI, its IDE extension, and the ChatGPT desktop app read `~/.codex/config.toml`:

```bash
codex mcp add context-manager -- context-manager
```

Or add it by hand:

```toml
[mcp_servers.context-manager]
command = "context-manager"
args = []
```

A project `.codex/config.toml` in a trusted project overrides the global one.

## Verify

Restart the client. The server works if these tools appear: `inspect_file`, `inspect_context`,
`project_scan`, `graph_get_dependencies`, `graph_impact_analysis`, and `graph_path_find`. As a quick
test, ask the agent to run `inspect_file` on any `.cs` file.

Then copy [`docs/AGENTS-template.md`](docs/AGENTS-template.md) into your project's `AGENTS.md` so
the agent uses the tools before reading files.

## Troubleshooting

| Symptom | Fix |
|---------|-----|
| `command not found`, or the server does not start | Put `~/.dotnet/tools` on `PATH`, or use the absolute path to the executable. |
| The tools do not appear | Quit the client completely and reopen it. |
| `project_scan` fails on a `net48` solution | Install VS Build Tools with the **.NET desktop build tools** workload (Windows only). |
| `project_scan` on `net48` fails with `TypeInitializationException` in `Microsoft.Build.Shared.XMakeElements` | Your newest Visual Studio ships an MSBuild this version does not support. Run `dotnet tool update -g ContextManager`. |
| The global config is ignored (Cursor, Kilo Code, Codex) | A project-level config overrides it. Look for `.cursor/mcp.json`, `kilo.jsonc`, or `.codex/config.toml` in the project. |
| JSON parse error | Remove trailing commas and comments. Only `kilo.jsonc` and `opencode.jsonc` allow comments. |

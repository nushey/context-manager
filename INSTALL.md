# Installing ContextManager

This guide covers installing the **ContextManager** MCP server and wiring it into every
supported client. ContextManager ships as a **.NET global tool** that speaks MCP over
**stdio** — every client below launches the same `context-manager` executable.

> Looking for Claude Code / Claude Desktop? Those are covered in the
> [README](README.md#2-add-to-your-mcp-client). This document adds native setup for
> **Codex, Antigravity, Kilo Code, Cursor, Windsurf, and Opencode**.

---

## 1. Prerequisites (all clients)

- **[.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10) or later** — verify with `dotnet --version`.
- That is the only requirement for `inspect_file` and `inspect_context`. No Python, Node, or Docker.
- **`project_scan` on `.NET Framework 4.8` solutions (Windows only)** additionally needs the
  MSBuild toolchain + Framework 4.8 targeting pack — install
  [Visual Studio Build Tools 2022/2026](https://visualstudio.microsoft.com/downloads/) with the
  **.NET desktop build tools** workload. These projects are evaluated with the MSBuild of the
  newest installed Visual Studio / Build Tools (17.x or 18.x), so a Visual Studio update can
  require a newer ContextManager release.
  `inspect_file` / `inspect_context` work on any platform regardless of target framework.

---

## 2. Install the tool (all clients)

```bash
dotnet tool install -g ContextManager
```

Verify:

```bash
context-manager --version
```

Update later with:

```bash
dotnet tool update -g ContextManager
```

### ⚠️ Windows / PATH note (read before configuring any client)

.NET global tools install to `~/.dotnet/tools` (Windows: `%USERPROFILE%\.dotnet\tools`).
Most clients spawn the configured `command` **without** a login shell, so the executable must be
resolvable on the process `PATH`. If a client reports *"command not found"* or the server fails to
start:

1. Confirm `~/.dotnet/tools` is on your `PATH` (the `dotnet tool install` output prints a warning if it is not).
2. Or use the **absolute path** as the command:
   - **Windows:** `C:\Users\<you>\.dotnet\tools\context-manager.exe`
   - **macOS / Linux:** `/Users/<you>/.dotnet/tools/context-manager` (or `/home/<you>/.dotnet/tools/context-manager`)

All snippets below use the bare `context-manager` command. Swap in the absolute path if your client
cannot find it on `PATH`.

---

## 3. Client configuration

| Client | Config file | Format | Server block key |
|--------|-------------|--------|------------------|
| [Codex](#codex) | `~/.codex/config.toml` | TOML | `[mcp_servers.*]` |
| [Antigravity](#antigravity) | `~/.gemini/config/mcp_config.json` | JSON | `mcpServers` |
| [Kilo Code](#kilo-code) | `~/.config/kilo/kilo.jsonc` | JSONC | `mcp` |
| [Cursor](#cursor) | `~/.cursor/mcp.json` (or project `.cursor/mcp.json`) | JSON | `mcpServers` |
| [Windsurf](#windsurf) | `~/.codeium/windsurf/mcp_config.json` | JSON | `mcpServers` |
| [Opencode](#opencode) | `opencode.json` (or `~/.config/opencode/opencode.json`) | JSON | `mcp` |

After editing a config file, **fully restart** the client (quit and reopen, not just reload) so it
re-reads the MCP configuration.

---

### Codex

OpenAI Codex CLI uses **TOML**. Each server is a `[mcp_servers.<name>]` table; environment variables
go in a nested `[mcp_servers.<name>.env]` table.

**Config file path**

| OS | Path |
|----|------|
| Windows | `%USERPROFILE%\.codex\config.toml` |
| macOS | `~/.codex/config.toml` |
| Linux | `~/.codex/config.toml` |

> A project-scoped `.codex/config.toml` is also honored in trusted projects.

**Add via the CLI (recommended):**

```bash
codex mcp add context-manager -- context-manager
```

**Or edit `config.toml` directly:**

```toml
[mcp_servers.context-manager]
command = "context-manager"
args = []
```

---

### Antigravity

Google Antigravity (IDE, CLI, and Antigravity 2.0) share a central **JSON** config under the
`mcpServers` key. You can open it from the IDE: agent panel → **`...`** dropdown → **Manage MCP Servers**
→ **View raw config**.

> Antigravity uses `serverUrl` (not `url`) for *remote* HTTP servers — irrelevant here since
> ContextManager is a local stdio server using `command`/`args`/`env`.

**Config file path**

| OS | Path |
|----|------|
| Windows | `%USERPROFILE%\.gemini\config\mcp_config.json` |
| macOS | `~/.gemini/config/mcp_config.json` |
| Linux | `~/.gemini/config/mcp_config.json` |

**Configuration:**

```json
{
  "mcpServers": {
    "context-manager": {
      "command": "context-manager"
    }
  }
}
```

---

### Kilo Code

Kilo Code stores MCP servers inside its main **JSONC** config under the `mcp` key. Note the distinct
schema: `command` is an **array**, env vars use `environment`, and each server has an explicit
`enabled` flag. Local stdio servers use `"type": "local"`.

**Config file path**

| Scope | OS | Path |
|-------|----|------|
| Global | Windows | `%APPDATA%\kilo\kilo.jsonc` |
| Global | macOS / Linux | `~/.config/kilo/kilo.jsonc` |
| Project | all | `kilo.jsonc` (or `.kilo/kilo.jsonc`) in the project root |

> Project-level config takes precedence over global.

**Configuration:**

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

---

### Cursor

Cursor uses **JSON** under the `mcpServers` key. Global config applies everywhere; a project
`.cursor/mcp.json` overrides it for that repo.

**Config file path**

| Scope | OS | Path |
|-------|----|------|
| Global | Windows | `%USERPROFILE%\.cursor\mcp.json` |
| Global | macOS / Linux | `~/.cursor/mcp.json` |
| Project | all | `.cursor/mcp.json` in the repo root |

**Configuration:**

```json
{
  "mcpServers": {
    "context-manager": {
      "command": "context-manager"
    }
  }
}
```

---

### Windsurf

Windsurf (Cascade) uses **JSON** under the `mcpServers` key. Open it from **Settings → Cascade → MCP
Servers → View raw config**, or edit the file directly.

**Config file path**

| OS | Path |
|----|------|
| Windows | `%USERPROFILE%\.codeium\windsurf\mcp_config.json` |
| macOS | `~/.codeium/windsurf/mcp_config.json` |
| Linux | `~/.codeium/windsurf/mcp_config.json` |

**Configuration:**

```json
{
  "mcpServers": {
    "context-manager": {
      "command": "context-manager"
    }
  }
}
```

---

### Opencode

Opencode uses **JSON** under the `mcp` key. Like Kilo Code, `command` is an **array**, env vars use
`environment`, and each server has an `enabled` flag. Local stdio servers use `"type": "local"`.

**Config file path**

| Scope | OS | Path |
|-------|----|------|
| Project | all | `opencode.json` (or `opencode.jsonc`) in the project root |
| Global | Windows | `%USERPROFILE%\.config\opencode\opencode.json` |
| Global | macOS / Linux | `~/.config/opencode/opencode.json` |

**Configuration:**

```json
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
```

---

## 4. Verify the connection

After restarting your client, the server is wired correctly if its tools appear:

- `inspect_file`
- `inspect_context`
- `project_scan`
- `graph_get_dependencies`
- `graph_impact_analysis`
- `graph_path_find`

A quick smoke test: ask the agent to run `inspect_file` on any absolute path to a `.cs` file and
confirm it returns a JSON contract. See the [README](README.md#tools) for tool semantics and output
examples.

## 5. Configure your agent's rules

Copy [`docs/AGENTS-template.md`](docs/AGENTS-template.md) into the `AGENTS.md` (or equivalent rules
file) of any project that uses ContextManager. It contains the mandatory usage rules for both the
inspection and graph tools so the agent navigates before it reads.

---

## Troubleshooting

| Symptom | Cause / Fix |
|---------|-------------|
| `command not found` / server won't start | `~/.dotnet/tools` not on `PATH`. Add it, or use the absolute path to the executable (see the [PATH note](#️-windows--path-note-read-before-configuring-any-client)). |
| Tools don't appear after editing config | Client wasn't fully restarted. Quit completely and reopen. |
| `project_scan` throws on a `net48` solution | Missing MSBuild / Framework 4.8 targeting pack. Install VS Build Tools with **.NET desktop build tools** (Windows only). |
| `project_scan` on a `net48` solution fails with `TypeInitializationException` in `Microsoft.Build.Shared.XMakeElements` | The newest installed Visual Studio ships an MSBuild that the installed ContextManager's Roslyn build host does not support. Run `dotnet tool update -g ContextManager`. |
| Wrong config wins (Cursor/Kilo/Codex) | Project-scoped config overrides global. Check for a local `.cursor/mcp.json`, `kilo.jsonc`, or `.codex/config.toml`. |
| JSON parse error | Trailing comma or comment in a strict-JSON file. Only `kilo.jsonc` / `opencode.jsonc` allow comments. |

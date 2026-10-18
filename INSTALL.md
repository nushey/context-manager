# Installing ContextManager

This guide installs the **ContextManager** MCP server and wires it into every supported client.
ContextManager ships as a **.NET global tool** that speaks MCP over **stdio**; every client below
launches the same `context-manager` executable.

The fastest path is the [one-line installer](#1-quick-install). The rest of this document covers
manual setup for **Claude Code, Claude Desktop, Codex, Antigravity, Kilo Code, Cursor, Devin, and
Opencode**.

---

## 1. Quick install

**macOS / Linux:**

```bash
curl -fsSL https://raw.githubusercontent.com/nushey/ContextManager/main/scripts/install.sh | bash
```

**Windows (PowerShell):**

```powershell
irm https://raw.githubusercontent.com/nushey/ContextManager/main/scripts/install.ps1 | iex
```

The installer:

1. Checks for `dotnet` with a .NET 10 SDK or later. Without one, it stops and points you to
   [https://dotnet.microsoft.com/download/dotnet/10](https://dotnet.microsoft.com/download/dotnet/10).
2. Runs `dotnet tool install -g ContextManager`, or `dotnet tool update -g ContextManager` when the
   tool is already installed.
3. Runs `context-manager setup`, a multi-select wizard that lists every client supported on your
   OS, with none preselected. The wizard tags an option `(detected)` when the client's config
   directory exists (for Claude Code, when `~/.claude.json` exists), and `(already configured)` when
   its config already has a `context-manager` entry.

**Non-interactive mode.** Pass `-y` (`--yes`; PowerShell: `-Yes`) to configure **all** clients
supported on the current OS, installed or not. The wizard creates any missing config directories
and files.

```bash
curl -fsSL https://raw.githubusercontent.com/nushey/ContextManager/main/scripts/install.sh | bash -s -- -y
```

```powershell
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/nushey/ContextManager/main/scripts/install.ps1))) -Yes
```

Once the tool is installed, you can run the wizard directly: `context-manager setup` or
`context-manager setup -y`. If there is no terminal and you omit `-y`, the wizard exits with code 1
and asks you to re-run it with `-y`.

**What the wizard writes.** The wizard edits only user-level (global) config files, the same ones
listed in [Client configuration](#4-client-configuration):

- **JSON / JSONC files:** the wizard inserts the `context-manager` entry into the root-level server
  collection (`mcpServers` or `mcp`) and preserves the rest of the file byte for byte, including
  comments, formatting, and key order. It creates a missing or empty file with the snippet
  documented below.
- **Codex:** the wizard appends the `[mcp_servers.context-manager]` table to `config.toml`.
- **Existing entries:** the wizard skips a client that already has a `context-manager` entry, so
  re-running is safe.
- **Unparseable files:** the wizard leaves them untouched and reports them as `failed`.

The wizard ends with a `configured / skipped / failed` summary (exit code 1 if any client failed) and warns
when `~/.dotnet/tools` (Windows: `%USERPROFILE%\.dotnet\tools`) is not on `PATH`.

**Shared config files.** Several surfaces of the same product read a single file, so the wizard
shows them as one option and writes the file once:

| Option | Surfaces sharing the file |
|--------|---------------------------|
| Codex | Codex CLI, IDE extension, ChatGPT desktop |
| Antigravity | Antigravity IDE, CLI, and Antigravity 2.0 |
| Devin | Devin Desktop (ex-Windsurf) and Devin CLI |

> **Claude Desktop on Linux:** the official docs give no Linux config path, so the installer does
> not offer it and `-y` skips it.

---

## 2. Prerequisites (manual install)

- **[.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10) or later** — verify with `dotnet --version`.
- `inspect_file` and `inspect_context` need nothing else: no Python, Node, or Docker.
- **`project_scan` on `.NET Framework 4.8` solutions (Windows only)** also needs the MSBuild
  toolchain and the Framework 4.8 targeting pack. Install
  [Visual Studio Build Tools 2022/2026](https://visualstudio.microsoft.com/downloads/) with the
  **.NET desktop build tools** workload. ContextManager evaluates these projects with the MSBuild of
  the newest installed Visual Studio or Build Tools (17.x or 18.x), so a Visual Studio update can
  require a newer ContextManager release. `inspect_file` and `inspect_context` work on any platform
  and any target framework.

---

## 3. Manual install of the tool

```bash
dotnet tool install -g ContextManager
```

Verify the install:

```bash
context-manager --version
```

Update it later:

```bash
dotnet tool update -g ContextManager
```

### ⚠️ Windows / PATH note (read before configuring any client)

.NET global tools install to `~/.dotnet/tools` (Windows: `%USERPROFILE%\.dotnet\tools`).
Most clients spawn the configured `command` **without** a login shell, so the process `PATH` must
resolve the executable. If a client reports *"command not found"* or the server fails to
start:

1. Confirm that `~/.dotnet/tools` is on your `PATH`; `dotnet tool install` prints a warning when it is missing.
2. Or use the **absolute path** as the command:
   - **Windows:** `C:\Users\<you>\.dotnet\tools\context-manager.exe`
   - **macOS / Linux:** `/Users/<you>/.dotnet/tools/context-manager` (or `/home/<you>/.dotnet/tools/context-manager`)

All snippets below use the bare `context-manager` command. Swap in the absolute path if your client
cannot find it on `PATH`.

---

## 4. Client configuration

| Client | Config file | Format | Server block key |
|--------|-------------|--------|------------------|
| [Claude Code](#claude-code) | `~/.claude.json` | JSON | `mcpServers` |
| [Claude Desktop](#claude-desktop) | `claude_desktop_config.json` | JSON | `mcpServers` |
| [Codex](#codex) | `~/.codex/config.toml` | TOML | `[mcp_servers.*]` |
| [Antigravity](#antigravity) | `~/.gemini/config/mcp_config.json` | JSON | `mcpServers` |
| [Kilo Code](#kilo-code) | `~/.config/kilo/kilo.jsonc` | JSONC | `mcp` |
| [Cursor](#cursor) | `~/.cursor/mcp.json` (or project `.cursor/mcp.json`) | JSON | `mcpServers` |
| [Devin](#devin) (ex-Windsurf) | `~/.config/devin/mcp_config.json` | JSON | `mcpServers` |
| [Opencode](#opencode) | `opencode.json` (or `~/.config/opencode/opencode.json`) | JSON | `mcp` |

After you edit a config file, **fully restart** the client (quit and reopen it; a reload is not
enough) so it re-reads the MCP configuration.

---

### Claude Code

Add the server at **user** scope so it is available in every project. Without `--scope user`,
`claude mcp add` uses the **local** scope (current project only).

```bash
claude mcp add --scope user context-manager -- context-manager
```

**Or edit the config file directly:** user-scoped servers live under the root-level `mcpServers` key
of `~/.claude.json` (Windows: `%USERPROFILE%\.claude.json`). Leave the `projects.*.mcpServers`
entries alone: those are per-project servers.

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

### Claude Desktop

**Config file path**

| OS | Path |
|----|------|
| Windows | `%APPDATA%\Claude\claude_desktop_config.json` |
| macOS | `~/Library/Application Support/Claude/claude_desktop_config.json` |
| Linux | Not supported (the official docs give no Linux path; the installer does not offer it) |

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

### Codex

Codex uses **TOML**. Each server is a `[mcp_servers.<name>]` table; environment variables
go in a nested `[mcp_servers.<name>.env]` table. The Codex CLI, the IDE extension, and the ChatGPT
desktop app all read this same file.

**Config file path**

| OS | Path |
|----|------|
| Windows | `%USERPROFILE%\.codex\config.toml` |
| macOS | `~/.codex/config.toml` |
| Linux | `~/.codex/config.toml` |

> Codex also reads a project-scoped `.codex/config.toml` in trusted projects.

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

The Google Antigravity IDE, CLI, and Antigravity 2.0 share one central **JSON** config under the
`mcpServers` key. To open it from the IDE, go to agent panel → **`...`** dropdown → **Manage MCP
Servers** → **View raw config**.

> Antigravity uses `serverUrl` (not `url`) for *remote* HTTP servers. That key does not apply here:
> ContextManager is a local stdio server configured with `command`/`args`/`env`.

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

Kilo Code stores MCP servers in its main **JSONC** config under the `mcp` key, with its own
schema: `command` is an **array**, env vars go in `environment`, each server has an explicit
`enabled` flag, and local stdio servers use `"type": "local"`.

**Config file path**

| Scope | OS | Path |
|-------|----|------|
| Global | Windows | `%USERPROFILE%\.config\kilo\kilo.jsonc` |
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

### Devin

Devin Desktop (formerly **Windsurf**, renamed on 2026-06-02) and the Devin CLI share one **JSON**
config under the `mcpServers` key.

**Config file path**

| OS | Path |
|----|------|
| Windows | `%APPDATA%\devin\mcp_config.json` |
| macOS / Linux | `$XDG_CONFIG_HOME/devin/mcp_config.json` (default `~/.config/devin/mcp_config.json`) |

> **Legacy:** `~/.codeium/windsurf/mcp_config.json` is the pre-rebrand Windsurf path. Current
> versions read the Devin path above, and the installer writes there.

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

Opencode uses **JSON** under the `mcp` key, with the same schema as Kilo Code: `command` is an
**array**, env vars go in `environment`, each server has an `enabled` flag, and local stdio servers
use `"type": "local"`.

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

## 5. Verify the connection

Restart your client. The server is wired correctly if its tools appear:

- `inspect_file`
- `inspect_context`
- `project_scan`
- `graph_get_dependencies`
- `graph_impact_analysis`
- `graph_path_find`

For a quick smoke test, ask the agent to run `inspect_file` on the absolute path of any `.cs` file
and confirm that it returns a JSON contract. The [README](README.md#tools) describes the tools and
shows output examples.

## 6. Configure your agent's rules

Copy [`docs/AGENTS-template.md`](docs/AGENTS-template.md) into the `AGENTS.md` (or equivalent rules
file) of any project that uses ContextManager. It contains the mandatory usage rules for both the
inspection and graph tools, so the agent navigates before it reads.

---

## Troubleshooting

| Symptom | Cause / Fix |
|---------|-------------|
| `command not found` / server won't start | `~/.dotnet/tools` is not on `PATH`. Add it, or use the absolute path to the executable (see the [PATH note](#️-windows--path-note-read-before-configuring-any-client)). |
| Tools don't appear after editing config | The client was not fully restarted. Quit it completely and reopen it. |
| `project_scan` throws on a `net48` solution | The MSBuild toolchain or the Framework 4.8 targeting pack is missing. Install VS Build Tools with the **.NET desktop build tools** workload (Windows only). |
| `project_scan` on a `net48` solution fails with `TypeInitializationException` in `Microsoft.Build.Shared.XMakeElements` | The newest installed Visual Studio ships an MSBuild that the installed ContextManager's Roslyn build host does not support. Run `dotnet tool update -g ContextManager`. |
| Wrong config wins (Cursor/Kilo/Codex) | A project-scoped config overrides the global one. Check for a local `.cursor/mcp.json`, `kilo.jsonc`, or `.codex/config.toml`. |
| JSON parse error | A strict-JSON file contains a trailing comma or a comment. Only `kilo.jsonc` / `opencode.jsonc` allow comments. |

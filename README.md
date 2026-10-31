<h1 align="center">ContextManager</h1>

<p align="center">
  <strong>Structural C# context for AI coding agents, powered by Roslyn.</strong><br>
  <em>Navigate before you read.</em>
</p>

<p align="center">
  <a href="#install">Install</a> &bull;
  <a href="#tools">Tools</a> &bull;
  <a href="#how-to-use-it">How to use it</a> &bull;
  <a href="#faq">FAQ</a> &bull;
  <a href="INSTALL.md">Full install guide</a>
</p>

---

ContextManager is an [MCP](https://modelcontextprotocol.io) server that saves an agent from reading
whole C# files. It does two things:

- **Inspects files.** It turns a `.cs` file into a JSON contract with its types, public members,
  constructor dependencies, base types, interfaces, and events. Method bodies, private members, and
  XML docs are left out; each method carries its line range so the agent can read only the body it
  needs.
- **Maps the solution.** It builds a knowledge graph of a `.sln` so the agent can find which files
  matter, and how risky a change is, before opening any of them.

## Install

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10) or later.

**macOS / Linux:**

```bash
curl -fsSL https://raw.githubusercontent.com/nushey/ContextManager/main/scripts/install.sh | bash
```

**Windows (PowerShell):**

```powershell
irm https://raw.githubusercontent.com/nushey/ContextManager/main/scripts/install.ps1 | iex
```

The installer installs or updates the `ContextManager` .NET global tool and runs
`context-manager setup`, a wizard that adds the server to the MCP clients you pick (Claude Code,
Claude Desktop, Codex, Antigravity, Kilo Code, Cursor, Devin, Opencode).

To install by hand instead:

```bash
dotnet tool install -g ContextManager
claude mcp add --scope user context-manager -- context-manager   # Claude Code
```

For other clients, flags, config-file paths, and troubleshooting, see [INSTALL.md](INSTALL.md).

> `project_scan` on `.NET Framework 4.8` solutions only works on Windows and needs
> [Visual Studio Build Tools](https://visualstudio.microsoft.com/downloads/) with the
> **.NET desktop build tools** workload. Everything else works on any platform.

## Tools

| Tool | What it returns |
|------|-----------------|
| `inspect_file` | The structural contract of one `.cs` file. |
| `inspect_context` | A compact view of up to 15 `.cs` files plus the references between them; `unresolved` lists the user types missing from the set. |
| `project_scan` | Builds or refreshes the solution's graph and reports its coverage. |
| `graph_get_dependencies` | The neighbors of a node, rolled up to their declaring type, with edge counts per kind. |
| `graph_impact_analysis` | Every type that depends on a node, directly or transitively, with its file. |
| `graph_path_find` | The shortest dependency path between two nodes. |

Graph tools take the path of the `.sln` and keep the graph up to date on their own: before
answering, they re-extract only what changed since the last scan. The graph is saved in
`.context-manager/<solution name>.graph.json` next to the `.sln`, so every session on that solution
shares it.

## How to use it

Start from the file the user mentions:

1. `inspect_file(path)`: read the type and build its node ID as `Namespace.TypeName`.
2. `graph_get_dependencies(solutionPath, nodeId)`: find the neighbors worth reading.
3. `inspect_file` on those neighbors only.
4. `graph_impact_analysis(solutionPath, nodeId)`: check the blast radius before changing anything.

A short impact list means the change is contained. A long one means the type's public contract is
load-bearing and should be preserved; it does not mean the agent has to read every file in it.

Node IDs use Roslyn's fully qualified format, for example `MyApp.Orders.OrderService` or
`MyApp.Orders.OrderService.GetOrderAsync(System.Guid)`. Pass the IDs that the graph tools return
as they are.

```json
// graph_impact_analysis("/abs/path/MyApp.sln", "MyApp.Orders.IOrderRepository")
{
  "affectedIds": ["MyApp.Orders.OrderService", "MyApp.Api.OrdersController"],
  "affectedFiles": {
    "MyApp.Orders.OrderService": "/abs/path/Orders/OrderService.cs",
    "MyApp.Api.OrdersController": "/abs/path/Api/OrdersController.cs"
  }
}
```

To make your agent follow this workflow, copy [`docs/AGENTS-template.md`](docs/AGENTS-template.md)
into the `AGENTS.md` of your project.

## FAQ

**Is it a background service?** No. It is an MCP stdio server: the client starts it when a session
opens and stops it when the session ends.

**What state does it keep?** Only the graph files written next to each `.sln`. The inspection tools
read the files you pass and keep nothing.

**Does it read my whole solution?** Only the graph tools do. `inspect_file` and `inspect_context`
read just the files you give them.

## Build and test

```bash
dotnet build
dotnet test
```

## License

MIT © nushey

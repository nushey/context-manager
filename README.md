<h1 align="center">ContextManager</h1>

<p align="center">
  <strong>Structural C# context for AI coding agents — powered by Roslyn.</strong><br>
  <em>Navigate before you read. Turn a 1,500-line file into a contract of tens of tokens, and a whole solution into a graph you can query.</em>
</p>

<p align="center">
  <a href="#installation">Installation</a> &bull;
  <a href="#add-to-your-mcp-client">MCP Clients</a> &bull;
  <a href="#tools">Tools</a> &bull;
  <a href="#how-it-works">How It Works</a> &bull;
  <a href="#faq">FAQ</a> &bull;
  <a href="docs/AGENTS-template.md">Agent Setup</a> &bull;
  <a href="INSTALL.md">Full Install Guide</a>
</p>

---

## What it is

A **structural contract** is the public shape of a C# file: its types and their kinds, the public
API surface, constructor dependencies, base types, interfaces, events, and the `using` map. It
leaves out method bodies, private members, and XML docs.

Reading a 1,500-line C# file costs an agent thousands of tokens on **every** call. ContextManager
is an [MCP](https://modelcontextprotocol.io) server that cuts this cost at two levels. It turns any
file into a compact JSON contract, and it builds a directed knowledge graph of the whole solution,
so the agent knows *which* files to open before it opens any.

## Data flow

```
   ┌─────────────────┐   MCP · stdio · JSON-RPC   ┌──────────────────────────┐
   │  Agent / Client │ ─────────────────────────► │  context-manager (.NET)  │
   │ Claude · Cursor │ ◄───────────────────────── │     Roslyn analysis      │
   └─────────────────┘        JSON contract        └────────────┬─────────────┘
                                                                 │ reads
                                                                 ▼
                                                   Workspace  *.cs  /  *.sln
```

The tools form two layers: **navigate** the graph to find the files, then **inspect** only those files:

```
Layer 1 — Navigate (graph tools)        Layer 2 — Inspect (file tools)
──────────────────────────────────      ─────────────────────────────
project_scan           → build map      inspect_file    → type signatures
graph_get_dependencies → neighbors      inspect_context → cross-file refs
graph_impact_analysis  → blast radius
graph_path_find        → hop sequence
```

---

## Installation

### Prerequisites

- **[.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10) or later** — verify with `dotnet --version`.

`inspect_file` and `inspect_context` need nothing else: no Python, Node, or Docker.

> **`project_scan` on `.NET Framework 4.8` solutions (Windows only):** `project_scan` loads these
> solutions through `MSBuildWorkspace`, which needs the MSBuild toolchain and the Framework 4.8
> targeting pack. Install [Visual Studio Build Tools 2022/2026](https://visualstudio.microsoft.com/downloads/)
> with the **.NET desktop build tools** workload. ContextManager evaluates these projects with the
> MSBuild of the newest installed Visual Studio or Build Tools (17.x or 18.x). Linux cannot scan
> `net48` solutions. `inspect_file` and `inspect_context` work on any platform and any target framework.

### Quick install (recommended)

One command checks for the .NET 10 SDK, installs or updates the `ContextManager` global tool, and
runs the `context-manager setup` wizard, where you pick the MCP clients to configure.

**macOS / Linux:**

```bash
curl -fsSL https://raw.githubusercontent.com/nushey/ContextManager/main/scripts/install.sh | bash
```

**Windows (PowerShell):**

```powershell
irm https://raw.githubusercontent.com/nushey/ContextManager/main/scripts/install.ps1 | iex
```

To configure **every** client supported on your OS without prompts:

```bash
curl -fsSL https://raw.githubusercontent.com/nushey/ContextManager/main/scripts/install.sh | bash -s -- -y
```

```powershell
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/nushey/ContextManager/main/scripts/install.ps1))) -Yes
```

Once the tool is installed, run the wizard any time with `context-manager setup` (or
`context-manager setup -y`). The wizard edits only user-level (global) config files. It inserts the
`context-manager` entry, leaves the rest of each file untouched, and skips clients that already have
the entry. See [INSTALL.md](INSTALL.md#1-quick-install) for details.

### Manual install (alternative)

```bash
dotnet tool install -g ContextManager
```

Verify the install, and update it later:

```bash
context-manager --version
dotnet tool update -g ContextManager
```

#### Add to your MCP client

**Claude Code (one command, all projects):**

```bash
claude mcp add --scope user context-manager -- context-manager
```

**Any `mcpServers`-style client (Claude Desktop, Cursor, Devin, Antigravity):**

```json
{
  "mcpServers": {
    "context-manager": {
      "command": "context-manager"
    }
  }
}
```

**[INSTALL.md](INSTALL.md)** lists the exact config-file path for each OS and the array-style
schema that some clients use:

| Client | Config file | Setup |
|--------|-------------|-------|
| Claude Code / Desktop | `~/.claude.json` / `claude_desktop_config.json` | [INSTALL.md](INSTALL.md#claude-code) |
| Codex | `~/.codex/config.toml` (TOML) | [INSTALL.md#codex](INSTALL.md#codex) |
| Antigravity | `~/.gemini/config/mcp_config.json` | [INSTALL.md#antigravity](INSTALL.md#antigravity) |
| Kilo Code | `~/.config/kilo/kilo.jsonc` | [INSTALL.md#kilo-code](INSTALL.md#kilo-code) |
| Cursor | `~/.cursor/mcp.json` | [INSTALL.md#cursor](INSTALL.md#cursor) |
| Devin (ex-Windsurf) | `~/.config/devin/mcp_config.json` | [INSTALL.md#devin](INSTALL.md#devin) |
| Opencode | `opencode.json` | [INSTALL.md#opencode](INSTALL.md#opencode) |

> **Windows / PATH:** .NET global tools live at `%USERPROFILE%\.dotnet\tools`. If a client reports
> *"command not found"*, put that directory on `PATH` or use the absolute path to
> `context-manager.exe`. See [INSTALL.md](INSTALL.md#3-manual-install-of-the-tool) for details.

---

## Tools

### Inspection

| Tool | Description |
|------|-------------|
| `inspect_file` | Returns a structural JSON contract for a single `.cs` file |
| `inspect_context` | Analyzes cross-file relationships across up to 15 `.cs` files using the Roslyn semantic model |

### Knowledge Graph

| Tool | Description |
|------|-------------|
| `project_scan` | Scans a `.sln`, builds the knowledge graph from all C# source, persists it to `<solution-root>/.context-manager/graph.json` |
| `graph_get_dependencies` | Neighbors of a node, aggregated at type granularity — member edges roll up to the declaring type, one entry per neighbor/direction with per-edge-kind counts (`edgeKinds`) |
| `graph_impact_analysis` | Every node that depends on a node, directly or transitively (backward BFS) |
| `graph_path_find` | The directed shortest path between two nodes as an ordered list of node IDs |

[How It Works](#how-it-works), the [graph reference](#knowledge-graph-reference), and
[Output examples](#output-examples) below document parameters and output shapes.

---

## How It Works

Always start from a file the user mentions:

1. **`inspect_file(path)`** → read the type and build its node ID as `namespace.TypeName`.
2. **`graph_get_dependencies(solutionPath, nodeId)`** → find the adjacent files that matter.
3. **`inspect_file`** → read only the relevant neighbors.
4. **`graph_impact_analysis(solutionPath, nodeId)`** → assess the blast radius *before* changing anything.

> **`graph_impact_analysis` is a risk-calibration tool, not a verification checklist.** A large
> result (50+ nodes) means the type's public contract is load-bearing and must be preserved — not
> that the agent should inspect all 50 files. Use the count and the direct callers from
> `graph_get_dependencies` to decide how conservative to be.

The graph tools need one scan first:

```
project_scan("/abs/path/to/MyApp.sln")
→ "Scan complete. 340 nodes, 850 edges."
```

`project_scan` saves the graph to `/abs/path/to/.context-manager/graph.json`. Every graph tool
takes the same `solutionPath`, so any session or agent working on that solution queries the latest
scan with no configuration. The server loads the graph on first use and reloads it whenever
`graph.json` changes. One server process can query several solutions side by side.

---

## Knowledge Graph reference

**`graph_get_dependencies`** — immediate neighbors of a node. Edges that land on a type's methods
or properties roll up to the declaring type, one entry per neighbor and direction. `edgeKinds`
counts edges of each kind; `direction` is `in` (the neighbor depends on the queried node) or `out`
(the queried node depends on the neighbor). The result never lists the queried type's own
members; `inspect_file` is canonical for those.

```json
// graph_get_dependencies("/abs/path/to/MyApp.sln", "MyApp.Orders.OrderService")
[
  { "id": "MyApp.Orders.IOrderRepository", "kind": "Interface", "direction": "out", "edgeKinds": { "INJECTS": 1, "CALLS": 3 } },
  { "id": "MyApp.Orders.IEventBus",        "kind": "Interface", "direction": "out", "edgeKinds": { "INJECTS": 1 } },
  { "id": "MyApp.Api.OrdersController",    "kind": "Class",     "direction": "in",  "edgeKinds": { "CALLS": 2, "REFERENCES": 1 } }
]
```

This works even for types consumed only through static method calls — the callers roll up to the
type, so a static helper shows its real consumers instead of an empty list.

**`graph_impact_analysis`** — how critical is this node? A short list means the change is
contained; a long list means the public contract is load-bearing.

```json
// graph_impact_analysis("/abs/path/to/MyApp.sln", "MyApp.Orders.IOrderRepository")
[
  "MyApp.Orders.OrderService",
  "MyApp.Api.OrdersController",
  "MyApp.Workers.OrderSyncWorker"
]
```

**`graph_path_find`** — how does the request reach the database?

```json
// graph_path_find("/abs/path/to/MyApp.sln", "MyApp.Api.OrdersController", "MyApp.Infrastructure.SqlOrderRepository")
[
  "MyApp.Api.OrdersController",
  "MyApp.Orders.OrderService",
  "MyApp.Orders.IOrderRepository",
  "MyApp.Infrastructure.SqlOrderRepository"
]
```

### Node ID format

Node IDs use Roslyn's `ISymbol.ToDisplayString()` format: the fully qualified symbol name. Pass the
exact string that `graph_get_dependencies` or `graph_impact_analysis` returns to the other graph tools.

- `MyApp.Orders.OrderService`
- `MyApp.Orders.IOrderRepository`
- `MyApp.Orders.OrderService.GetOrderAsync(System.Guid)`

---

## FAQ

> **Does it run as a background service or daemon?**
> No. It is an MCP **stdio** server: a child process that the client spawns and talks to over
> stdin/stdout. You host nothing, open no port, and write no `systemd` unit.

> **When does the process start and stop?**
> The MCP client launches `context-manager` when your session or tool connection starts, and kills
> it when the session ends. You start it yourself only to run `context-manager --version` or
> `context-manager setup`.

> **Where is state stored? Is it stateful?**
> `inspect_file` and `inspect_context` are pure functions of the files you pass; they keep no state.
> The only persisted state is the knowledge graph, which `project_scan` writes to
> `<solution-root>/.context-manager/graph.json`. Graph tools read it by `solutionPath`; rescan any time.

> **Does it read my whole solution or follow `<ProjectReference>` edges?**
> Only `project_scan` is solution-wide. `inspect_file` and `inspect_context` read only the files you
> pass; the analyzer never crosses project-reference boundaries on its own.

> **Why don't I see method bodies, private members, or XML docs?**
> ContextManager excludes them by design: they are the tokens you want to *avoid*. The contract
> gives the exact `startLine`/`endLine` of each method, so the agent can `read_file` only the body
> it needs. Two inclusions are deliberate, because both are reachable contract: explicit interface
> implementations (reported with their qualified name, e.g. `IFoo.Bar`) and public events.

> **Cross-platform?**
> Yes. Inspection works anywhere .NET 10 runs. The one exception is `project_scan` on `net48`
> solutions, which is Windows-only because it needs the MSBuild toolchain (see Prerequisites).

> **How much does it actually save?**
> A contract costs tens of tokens; a raw file read costs thousands. The graph also lets the agent
> skip the files it does not need.

---

## Output examples

### `inspect_file`

This output comes from the `ModernCSharpFeatures.cs` test fixture and shows the detail fields
(`isPartial`, `isRequired`, `accessors`, `genericConstraints`):

```json
{
  "file": "ModernCSharpFeatures.cs",
  "namespace": "ContextManager.Analysis.Tests.Fixtures",
  "usings": [],
  "types": [
    {
      "name": "PartialOrderService",
      "kind": "class",
      "access": "public",
      "isPartial": true,
      "constructorDependencies": [
        { "type": "string", "name": "customerName" }
      ],
      "methods": [
        {
          "name": "Process",
          "access": "public",
          "returnType": "void",
          "startLine": 10,
          "endLine": 10,
          "parameters": [
            { "type": "string?", "name": "orderId" }
          ]
        }
      ],
      "properties": [
        { "name": "CustomerName", "type": "string?", "access": "public", "accessors": "get; set;" }
      ]
    },
    {
      "name": "CustomerProfile",
      "kind": "class",
      "access": "public",
      "constructorDependencies": [
        { "type": "string", "name": "email" },
        { "type": "string", "name": "fullName" }
      ],
      "methods": [
        {
          "name": "GetDisplayName",
          "access": "public",
          "returnType": "string",
          "startLine": 22,
          "endLine": 22
        }
      ],
      "properties": [
        { "name": "Email",       "type": "string",  "access": "public", "isRequired": true, "accessors": "get; set;" },
        { "name": "FullName",    "type": "string",  "access": "public", "isRequired": true, "accessors": "get; set;" },
        { "name": "PhoneNumber", "type": "string?", "access": "public", "accessors": "get; set;" }
      ]
    },
    {
      "name": "GenericProcessor",
      "kind": "class",
      "access": "public",
      "methods": [
        {
          "name": "Convert",
          "access": "public",
          "returnType": "T",
          "startLine": 28,
          "endLine": 31,
          "parameters": [
            { "type": "object", "name": "input" }
          ],
          "genericConstraints": ["where T : class, new()"]
        },
        {
          "name": "Map",
          "access": "public",
          "returnType": "TResult",
          "startLine": 33,
          "endLine": 36,
          "parameters": [
            { "type": "TSource", "name": "source" }
          ],
          "genericConstraints": ["where TSource : notnull", "where TResult : class"]
        }
      ]
    },
    {
      "name": "OrderSummary",
      "kind": "record",
      "access": "public",
      "constructorDependencies": [
        { "type": "string",   "name": "OrderId" },
        { "type": "decimal",  "name": "Total" },
        { "type": "string?",  "name": "Notes" }
      ]
    }
  ]
}
```

### `inspect_context`

This representative output follows the `ContextAnalysis` model. It shows the cross-file references
resolved when you analyze `OrderService.cs` and `IOrderRepository.cs` together:

```json
{
  "files": [
    {
      "file": "OrderService.cs",
      "namespace": "MyApp.Orders",
      "types": [
        {
          "name": "OrderService",
          "kind": "class",
          "base": null,
          "implements": ["IOrderService"],
          "attributes": null,
          "constructorDependencies": ["IOrderRepository"],
          "methods": ["Task<Order> GetOrderAsync(Guid id)", "Task CreateAsync(CreateOrderRequest request)"]
        }
      ]
    },
    {
      "file": "IOrderRepository.cs",
      "namespace": "MyApp.Orders",
      "types": [
        {
          "name": "IOrderRepository",
          "kind": "interface",
          "base": null,
          "implements": null,
          "attributes": null,
          "constructorDependencies": null,
          "methods": ["Task<Order> GetOrderAsync(Guid id)", "Task SaveAsync(Order order)"]
        }
      ]
    }
  ],
  "references": [
    {
      "from": "OrderService",
      "to": "IOrderRepository",
      "via": "constructor",
      "resolvedFile": "IOrderRepository.cs"
    }
  ],
  "unresolved": ["IOrderService"]
}
```

`unresolved` lists only user-defined types missing from the input set. ContextManager resolves BCL
and framework types (`Task`, `CancellationToken`, `string`, …) against framework metadata and leaves
them out. The output is compressed on purpose (methods as one-line strings, no properties, no line
numbers); use `inspect_file` when you need full member detail.

---

## Configuring your agent

Copy [`docs/AGENTS-template.md`](docs/AGENTS-template.md) into the `AGENTS.md` of any project that
uses ContextManager. It contains the mandatory rules for both the inspection and graph tools, so
the agent navigates before it reads.

## Build & test

```bash
dotnet restore
dotnet build
dotnet test
```

## License

MIT © nushey

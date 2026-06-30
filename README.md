<h1 align="center">ContextManager</h1>

<p align="center">
  <strong>Structural C# context for AI coding agents — powered by Roslyn.</strong><br>
  <em>Navigate before you read. Turn a 1 500-line file into a contract of tens of tokens, and a whole solution into a graph you can query.</em>
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

A **structural contract** is the public shape of a C# file with the noise removed: the types,
their kind, the full public API surface, constructor dependencies, base types, interfaces, events,
and `using` map — **but not** method bodies, private members, or XML docs.

Reading a 1 500-line C# file costs an agent thousands of tokens on **every** call. ContextManager
is an [MCP](https://modelcontextprotocol.io) server that solves this at two levels: it turns any
file into a compact JSON contract, and it builds a directed knowledge graph of the entire solution
so the agent knows *which* files to open before opening anything.

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

Two layers, one mental model — **navigate** the graph to find the files, then **inspect** only those:

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

That's it for `inspect_file` and `inspect_context`. No Python, no Node, no Docker.

> **`project_scan` on `.NET Framework 4.8` solutions (Windows only):** it uses `MSBuildWorkspace`,
> which needs the MSBuild toolchain + Framework 4.8 targeting pack. Install
> [Visual Studio Build Tools 2022/2025](https://visualstudio.microsoft.com/downloads/) with the
> **.NET desktop build tools** workload (MSBuild 17.x and 18.x both supported). Not supported on
> Linux for `net48`. `inspect_file` / `inspect_context` work on any platform regardless of target framework.

### 1. Install the tool

```bash
dotnet tool install -g ContextManager
```

Verify, and update later:

```bash
context-manager --version
dotnet tool update -g ContextManager
```

### Add to your MCP client

**Claude Code (one command):**

```bash
claude mcp add context-manager -- context-manager
```

**Any `mcpServers`-style client (`.mcp.json`, Claude Desktop, Cursor, Windsurf, Antigravity):**

```json
{
  "mcpServers": {
    "context-manager": {
      "command": "context-manager"
    }
  }
}
```

For exact config-file paths (per OS), the array-style schema some clients use, and the
pre-loaded-graph variants, see **[INSTALL.md](INSTALL.md)**:

| Client | Config file | Setup |
|--------|-------------|-------|
| Claude Code / Desktop | `.mcp.json` / `claude_desktop_config.json` | [INSTALL.md](INSTALL.md) |
| Codex | `~/.codex/config.toml` (TOML) | [INSTALL.md#codex](INSTALL.md#codex) |
| Antigravity | `~/.gemini/config/mcp_config.json` | [INSTALL.md#antigravity](INSTALL.md#antigravity) |
| Kilo Code | `~/.config/kilo/kilo.jsonc` | [INSTALL.md#kilo-code](INSTALL.md#kilo-code) |
| Cursor | `~/.cursor/mcp.json` | [INSTALL.md#cursor](INSTALL.md#cursor) |
| Windsurf | `~/.codeium/windsurf/mcp_config.json` | [INSTALL.md#windsurf](INSTALL.md#windsurf) |
| Opencode | `opencode.json` | [INSTALL.md#opencode](INSTALL.md#opencode) |

> **Windows / PATH:** .NET global tools live at `%USERPROFILE%\.dotnet\tools`. If a client reports
> *"command not found"*, put that directory on `PATH` or use the absolute path to
> `context-manager.exe`. Details in [INSTALL.md](INSTALL.md#2-install-the-tool-all-clients).

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
| `graph_impact_analysis` | BFS backward from a node — every node that directly or transitively depends on it |
| `graph_path_find` | The directed shortest path between two nodes as an ordered list of node IDs |

Parameters and output shapes are documented in [How It Works](#how-it-works), the
[graph reference](#knowledge-graph-reference), and [Output examples](#output-examples) below.

---

## How It Works

The starting point is always a file the user mentions. From there:

1. **`inspect_file(path)`** → read the type → construct its node ID as `namespace.TypeName`.
2. **`graph_get_dependencies(nodeId)`** → discover which adjacent files matter for context.
3. **`inspect_file`** the relevant neighbors — only those, nothing else.
4. **`graph_impact_analysis(nodeId)`** → assess the blast radius *before* changing anything.

> **`graph_impact_analysis` is a risk-calibration tool, not a verification checklist.** A large
> result (50+ nodes) means the type's public contract is load-bearing and must be preserved — not
> that the agent should inspect all 50 files. Use the count and the direct callers from
> `graph_get_dependencies` to decide how conservative to be.

To use the graph at all, scan once:

```
project_scan("/abs/path/to/MyApp.sln")
→ "Scan complete. 340 nodes, 850 edges."
```

The graph is saved to `/abs/path/to/.context-manager/graph.json` and kept in memory for the session.

---

## Knowledge Graph reference

**`graph_get_dependencies`** — immediate neighbors of a node. Edges that land on a type's methods
or properties roll up to the declaring type, one entry per neighbor and direction. `edgeKinds`
counts edges of each kind; `direction` is `in` (the neighbor depends on the queried node) or `out`
(the queried node depends on the neighbor). The queried type's own members are never listed —
`inspect_file` is canonical for those.

```json
// graph_get_dependencies("MyApp.Orders.OrderService")
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
// graph_impact_analysis("MyApp.Orders.IOrderRepository")
[
  "MyApp.Orders.OrderService",
  "MyApp.Api.OrdersController",
  "MyApp.Workers.OrderSyncWorker"
]
```

**`graph_path_find`** — how does the request reach the database?

```json
// graph_path_find("MyApp.Api.OrdersController", "MyApp.Infrastructure.SqlOrderRepository")
[
  "MyApp.Api.OrdersController",
  "MyApp.Orders.OrderService",
  "MyApp.Orders.IOrderRepository",
  "MyApp.Infrastructure.SqlOrderRepository"
]
```

### Node ID format

Node IDs use Roslyn's `ISymbol.ToDisplayString()` format — the fully qualified type name. Use the
exact string returned by `graph_get_dependencies` or `graph_impact_analysis` as input to other graph tools.

- `MyApp.Orders.OrderService`
- `MyApp.Orders.IOrderRepository`
- `MyApp.Orders.OrderService.GetOrderAsync(System.Guid)`

### Pre-loading the graph at startup

To make the graph available immediately without calling `project_scan` manually, pass it at
startup. The `--graph` arg and `CONTEXT_MANAGER_GRAPH_PATH` env var apply to **every** client —
see [INSTALL.md](INSTALL.md) for each client's syntax.

```json
{
  "mcpServers": {
    "context-manager": {
      "command": "context-manager",
      "args": ["--graph", "/abs/path/to/.context-manager/graph.json"]
    }
  }
}
```

```json
{
  "mcpServers": {
    "context-manager": {
      "command": "context-manager",
      "env": { "CONTEXT_MANAGER_GRAPH_PATH": "/abs/path/to/.context-manager/graph.json" }
    }
  }
}
```

---

## FAQ

> **Does it run as a background service or daemon?**
> No. It's an MCP **stdio** server — a child process the client spawns and talks to over
> stdin/stdout. Nothing to host, no port, no `systemd` unit.

> **When does the process start and stop?**
> The MCP client launches `context-manager` when your session/tool connection starts and kills it
> when the session ends. You never start it manually (except `--version` to smoke-test the install).

> **Where is state stored? Is it stateful?**
> `inspect_file` and `inspect_context` are pure functions of the files you pass — zero state. The
> only persistence is the knowledge graph, written by `project_scan` to
> `<solution-root>/.context-manager/graph.json`. Pre-load it at startup or rebuild it any time.

> **Does it read my whole solution or follow `<ProjectReference>` edges?**
> Only `project_scan` is solution-wide. `inspect_file` / `inspect_context` are scoped strictly to
> the file(s) you pass — the analyzer never crosses project-reference boundaries on its own.

> **Why don't I see method bodies, private members, or XML docs?**
> Excluded by design — they're the tokens you're trying to *avoid*. The contract gives you exact
> `startLine`/`endLine` per method so the agent can `read_file` only the body it actually needs.
> Two deliberate inclusions: explicit interface implementations (reported with their qualified name,
> e.g. `IFoo.Bar`) and public events — both are reachable contract.

> **Cross-platform?**
> Yes. Inspection works anywhere .NET 10 runs. The one exception is `project_scan` on `net48`
> solutions, which is Windows-only because it needs the MSBuild toolchain (see Prerequisites).

> **How much does it actually save?**
> A contract is tens of tokens versus the thousands a raw file read costs — and the graph lets the
> agent skip opening files it doesn't need at all.

---

## Output examples

### `inspect_file`

Derived from `ModernCSharpFeatures.cs`, showing the detail fields (`isPartial`, `isRequired`,
`accessors`, `genericConstraints`):

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

A representative output matching the `ContextAnalysis` model shape — cross-file references resolved
when `OrderService.cs` and `IOrderRepository.cs` are analyzed together:

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

`unresolved` lists only user-defined types missing from the input set — BCL/framework types
(`Task`, `CancellationToken`, `string`, …) are resolved against framework metadata and excluded.
The output is intentionally compressed (methods as one-line strings, no properties, no line
numbers): use `inspect_file` when you need full member detail.

---

## Configuring your agent

Copy [`docs/AGENTS-template.md`](docs/AGENTS-template.md) into the `AGENTS.md` of any project that
uses context-manager. It contains the mandatory rules for both the inspection and graph tools, so
the agent navigates before it reads.

## Build & test

```bash
dotnet restore
dotnet build
dotnet test
```

## License

MIT © nushey

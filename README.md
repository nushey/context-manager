# ContextManager MCP

An MCP (Model Context Protocol) server that extracts structural contracts from C# source files using Roslyn — and builds a knowledge graph of the whole solution so agents navigate before they read.

## Why

Reading a 1 500-line C# file costs an agent thousands of tokens on every call. ContextManager solves this at two levels:

**Inspection** — turn a file into a compact JSON contract (tens of tokens) that tells the agent:
- What types exist and their kind (`class`, `record`, `interface`, `enum`, `dto`)
- The full public API surface — methods with return types, parameters, and decorators
- Exact `startLine`/`endLine` for each method, so the agent can read only the body it needs
- Constructor dependencies (the DI graph)
- Base classes and interfaces
- All `using` directives (the import map)
- `partial` class detection, `required` property flags, and generic method constraints

**Navigation** — build a Directed Property Graph of the entire solution so the agent knows *which* files to inspect before reading anything:
- Scan once, query forever
- Ask "who depends on `OrderService`?" or "what breaks if I change `IOrderRepository`?"
- Follow topological paths from controller to database without opening a single file

## Tools

### Inspection

| Tool | Description |
|------|-------------|
| `inspect_file` | Returns a structural JSON contract for a single `.cs` file |
| `inspect_context` | Analyzes cross-file relationships in up to 15 `.cs` files using the Roslyn semantic model |

### Knowledge Graph

| Tool | Description |
|------|-------------|
| `project_scan` | Scans a `.sln`, builds the knowledge graph from all C# source files, and persists it to `<solution-root>/.context-manager/graph.json` |
| `graph_get_dependencies` | Returns immediate neighbors of a node (all incoming and outgoing edges) as compressed node contracts |
| `graph_impact_analysis` | BFS backward from a node — returns every node that directly or transitively depends on it |
| `graph_path_find` | Returns the directed shortest path between two nodes as an ordered list of node IDs |

## Installation

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8) or later — verify with `dotnet --version`

That's it. No Python, no Node, no Docker.

### 1. Install the tool

```bash
dotnet tool install -g ContextManager
```

Verify the install:

```bash
context-manager --version
```

### 2. Add to your MCP client

**Claude Code (recommended — one command):**

```bash
claude mcp add context-manager -- context-manager
```

**Claude Code (manual — add to `.mcp.json` in your project root):**

```json
{
  "mcpServers": {
    "context-manager": {
      "command": "context-manager"
    }
  }
}
```

**Claude Desktop — add to `claude_desktop_config.json`:**

```json
{
  "mcpServers": {
    "context-manager": {
      "command": "context-manager"
    }
  }
}
```

### Updating

```bash
dotnet tool update -g ContextManager
```

## Knowledge Graph

The graph tools work as a two-layer navigation system:

```
Layer 1 — Navigate (graph tools)       Layer 2 — Inspect (file tools)
─────────────────────────────────      ─────────────────────────────
project_scan          → build map      inspect_file    → type signatures
graph_get_dependencies → neighbors     inspect_context → cross-file refs
graph_impact_analysis  → blast radius
graph_path_find        → hop sequence
```

**Typical agent workflow:**

The starting point is always a file the user mentions. From there:

1. `inspect_file(path)` → read the type → construct node ID as `namespace.TypeName`
2. `graph_get_dependencies(nodeId)` → discover which adjacent files to read for context
3. `inspect_file` on the neighbors that are relevant — only those, nothing else
4. `graph_impact_analysis(nodeId)` → assess the blast radius before making changes

**Important:** `graph_impact_analysis` is a risk calibration tool, not a verification checklist. A large result (50+ nodes) means the type's public contract must be preserved — not that the agent should inspect all 50 files. The agent uses the count and the direct callers to decide how conservative to be.

### Step 1 — Scan the solution

```
project_scan("/abs/path/to/MyApp.sln")
→ "Scan complete. 340 nodes, 850 edges."
```

The graph is saved to `/abs/path/to/.context-manager/graph.json` and kept in memory for the session.

### Step 2 — Query the graph

**`graph_get_dependencies`** — who are the immediate neighbors of a node?

```json
// graph_get_dependencies("MyApp.Orders.OrderService")
[
  { "id": "MyApp.Api.OrdersController",          "kind": "Class" },
  { "id": "MyApp.Orders.IOrderRepository",       "kind": "Interface" },
  { "id": "MyApp.Orders.IEventBus",              "kind": "Interface" }
]
```

**`graph_impact_analysis`** — how critical is this node? What is the blast radius of a change?

```json
// graph_impact_analysis("MyApp.Orders.IOrderRepository")
[
  "MyApp.Orders.OrderService",
  "MyApp.Api.OrdersController",
  "MyApp.Workers.OrderSyncWorker"
]
```

A short list means the change is contained. A long list means the node's public contract is load-bearing — preserve it. The agent reads only the direct callers from `graph_get_dependencies`, not every node in this list.

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

### Step 3 — Inspect only what matters

Use the `file` paths from `graph_get_dependencies` results to call `inspect_file` on the files you actually need to understand. The graph tells you where to look; the inspection tools tell you what's there.

### Configuring your agent

Copy [`docs/AGENTS-template.md`](docs/AGENTS-template.md) into the `AGENTS.md` of any project that uses context-manager. It contains the mandatory rules for both inspection and graph tools.

### Pre-loading the graph at startup

If you scan often and want the graph available immediately without calling `project_scan` manually, pass the graph path at startup:

**Claude Code (`.mcp.json`):**

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

Or via environment variable:

```json
{
  "mcpServers": {
    "context-manager": {
      "command": "context-manager",
      "env": {
        "CONTEXT_MANAGER_GRAPH_PATH": "/abs/path/to/.context-manager/graph.json"
      }
    }
  }
}
```

### Node ID format

Node IDs use Roslyn's `ISymbol.ToDisplayString()` format — the fully qualified type name. Examples:

- `MyApp.Orders.OrderService`
- `MyApp.Orders.IOrderRepository`
- `MyApp.Orders.OrderService.GetOrderAsync(System.Guid)`

Use the exact string returned by `graph_get_dependencies` or `graph_impact_analysis` as input to other graph tools.

## Output examples

### `inspect_file`

The example below is derived from `ModernCSharpFeatures.cs` and shows the new fields (`isPartial`, `isRequired`, `genericConstraints`):

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
        { "name": "CustomerName", "type": "string?", "access": "public" }
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
        { "name": "Email",       "type": "string",  "access": "public", "isRequired": true },
        { "name": "FullName",    "type": "string",  "access": "public", "isRequired": true },
        { "name": "PhoneNumber", "type": "string?", "access": "public" }
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
          "genericConstraints": ["T : class, new()"]
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
          "genericConstraints": ["TSource : notnull", "TResult : class"]
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

The example below is a representative output matching the `ContextAnalysis` model shape. It shows how cross-file references are resolved when `OrderService.cs` and `IOrderRepository.cs` are analyzed together:

```json
{
  "files": [
    {
      "file": "OrderService.cs",
      "namespace": "Zureo.Orders",
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
      "namespace": "Zureo.Orders",
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

## Build & test

```bash
dotnet restore
dotnet build
dotnet test
```

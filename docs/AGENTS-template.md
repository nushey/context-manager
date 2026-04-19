# ContextManager MCP — AGENTS Template

Copy this file into your repository as `AGENTS.md` (or merge it into an existing one) to give your AI coding agents accurate, up-to-date documentation for the ContextManager MCP tools.

---

## Overview

ContextManager is an MCP server that extracts structural contracts from C# source files using Roslyn. It exposes two tools:

| Tool | Purpose |
|------|---------|
| `inspect_file` | Structural JSON for a **single** `.cs` file — types, methods, properties, dependencies |
| `inspect_context` | Cross-file relationship graph for **up to 15** `.cs` files — resolved references, dependency edges |

Both tools are **read-only and stateless**. They never modify files and hold no in-process state between calls.

---

## Installation

```bash
dotnet tool install -g ContextManager
```

---

## `.mcp.json` snippet

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

## Tools reference

### `inspect_file`

Returns a structural JSON contract for a single C# source file.

#### Input schema

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `filePath` | `string` | yes | Absolute or working-directory-relative path to a `.cs` file |

#### Example call

```json
{ "filePath": "/src/Orders/OrderService.cs" }
```

#### Example output

```json
{
  "file": "OrderService.cs",
  "namespace": "Zureo.Orders",
  "usings": ["System", "System.Threading.Tasks", "Zureo.Common"],
  "types": [
    {
      "name": "OrderService",
      "kind": "class",
      "access": "public",
      "base": null,
      "implements": ["IOrderService"],
      "attributes": ["Scoped"],
      "constructorDependencies": [
        { "type": "IOrderRepository", "name": "repository" },
        { "type": "ILogger<OrderService>", "name": "logger" }
      ],
      "methods": [
        {
          "name": "GetOrderAsync",
          "access": "public",
          "returnType": "Task<Order>",
          "startLine": 18,
          "endLine": 23,
          "parameters": [
            { "type": "Guid", "name": "id" }
          ]
        }
      ],
      "properties": [
        { "name": "MaxRetries", "type": "int", "access": "public", "isRequired": true }
      ]
    }
  ]
}
```

#### Output field reference

| Field | Notes |
|-------|-------|
| `file` | Filename (not full path) |
| `namespace` | File-level namespace declaration, or `null` |
| `usings` | All `using` directives, in declaration order |
| `types[].kind` | One of: `class`, `record`, `struct`, `interface`, `enum`, `dto` |
| `types[].isPartial` | `true` when the `partial` modifier is present; omitted otherwise |
| `types[].constructorDependencies` | Parameters of the primary/explicit constructor |
| `methods[].startLine` / `endLine` | 1-based line numbers — use these to read only the method body you need |
| `methods[].genericConstraints` | One entry per `where` clause, e.g. `"T : class, new()"` |
| `properties[].isRequired` | `true` when the `required` modifier is present; omitted otherwise |

---

### `inspect_context`

Analyzes cross-file relationships across up to 15 C# source files using the Roslyn semantic model. Returns a dependency graph: which types reference which other types, and whether those references resolve within the provided file set.

#### Input schema

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `filePaths` | `string[]` | yes | List of absolute paths to `.cs` files (max 15) |

#### Example call

```json
{
  "filePaths": [
    "/src/Orders/OrderService.cs",
    "/src/Orders/IOrderRepository.cs"
  ]
}
```

#### Example output

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

#### Output field reference

| Field | Notes |
|-------|-------|
| `files` | One entry per input file; same structure as `inspect_file` minus `usings` |
| `references[].from` | Type that declares the dependency |
| `references[].to` | Type being depended on |
| `references[].via` | How the dependency is declared: `constructor`, `property`, `field`, or `method` |
| `references[].resolvedFile` | Filename if `to` was found in the input set; `null` if unresolved |
| `unresolved` | Type names referenced in the input files but not found in the input set |

---

## When NOT to call these tools

Avoid calling `inspect_file` or `inspect_context` when:

- **You need method bodies.** Both tools intentionally exclude implementation code. Use your editor or `read_file` to read a specific method body using the `startLine`/`endLine` from `inspect_file` output.
- **You need cross-project symbol resolution.** The analyzer scope is limited to the files you pass in. Types from referenced NuGet packages or other projects will appear in `unresolved`.
- **You need runtime behavior or reflection metadata.** These tools perform static syntactic analysis only — no execution, no runtime type information.
- **You need XML doc comments.** Doc comments are excluded by design.
- **You need private members.** Only public and internal members are included in the output.
- **You are analyzing non-C# files.** Only `.cs` files are supported.
- **You have more than 15 files for `inspect_context`.** Split into smaller batches or use `inspect_file` per file instead.

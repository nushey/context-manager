# ContextManager MCP

An MCP (Model Context Protocol) server that extracts structural contracts from C# source files using Roslyn. Instead of sending thousands of tokens of raw source code to an AI agent, you give it a compact JSON summary: types, methods, signatures, dependencies, line numbers — everything an agent needs to understand a file without reading it.

## Why

Reading a 1 500-line C# file costs an agent thousands of tokens on every call. ContextManager turns that file into a focused JSON contract (tens of tokens) that tells the agent:

- What types exist and their kind (`class`, `record`, `interface`, `enum`, `dto`)
- The full public API surface — methods with return types, parameters, and decorators
- Exact `startLine`/`endLine` for each method, so the agent can read only the body it needs
- Constructor dependencies (the DI graph)
- Base classes and interfaces
- All `using` directives (the import map)
- `partial` class detection, `required` property flags, and generic method constraints

## Tools

| Tool | Description |
|------|-------------|
| `inspect_file` | Returns a structural JSON contract for a single `.cs` file |
| `inspect_context` | Analyzes cross-file relationships in up to 15 `.cs` files using the Roslyn semantic model |

## Installation

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8)

### Install as a global dotnet tool

```bash
dotnet tool install -g ContextManager
```

After installation the server is available as:

```bash
context-manager
```

### Claude Code

Add to your project `.mcp.json`:

```json
{
  "mcpServers": {
    "context-manager": {
      "command": "context-manager"
    }
  }
}
```

### Claude Desktop

Add to your `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "context-manager": {
      "command": "context-manager"
    }
  }
}
```

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

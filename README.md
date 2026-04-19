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

## Tools

| Tool | Description |
|------|-------------|
| `inspect_file` | Returns a structural JSON contract for a single `.cs` file |

## Installation

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8)

### Claude Code

Add to your project `.mcp.json`:

```json
{
  "mcpServers": {
    "context-manager": {
      "command": "dotnet",
      "args": [
        "run",
        "--no-build",
        "--project",
        "/absolute/path/to/ContextManager/src/ContextManager.Mcp/ContextManager.Mcp.csproj"
      ]
    }
  }
}
```

Build once before starting Claude Code:

```bash
dotnet build /absolute/path/to/ContextManager/ContextManager.sln
```

### Claude Desktop

Add to your `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "context-manager": {
      "command": "dotnet",
      "args": [
        "run",
        "--no-build",
        "--project",
        "/absolute/path/to/ContextManager/src/ContextManager.Mcp/ContextManager.Mcp.csproj"
      ]
    }
  }
}
```

### MCP Inspector (manual testing)

```bash
dotnet build
npx @modelcontextprotocol/inspector dotnet run --no-build --project src/ContextManager.Mcp/ContextManager.Mcp.csproj
```

## Output example

```json
{
  "file": "OrderService.cs",
  "namespace": "Zureo.Orders",
  "usings": ["System", "Zureo.Common"],
  "types": [{
    "name": "OrderService",
    "kind": "class",
    "access": "public",
    "base": "ApiControllerBase",
    "implements": ["IOrderService"],
    "constructorDependencies": [
      { "type": "IOrderRepository", "name": "repository" }
    ],
    "methods": [{
      "name": "GetOrder",
      "access": "public",
      "returnType": "void",
      "startLine": 24,
      "endLine": 41,
      "parameters": [
        { "type": "ScriptRequest", "name": "request" },
        { "type": "ScriptResponse", "name": "response" }
      ]
    }]
  }]
}
```

## Build & test

```bash
dotnet restore
dotnet build
dotnet test
```

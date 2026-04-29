# 007 — Register ContextAnalyzer and CrossReferenceResolver in DI

## Files
- `src/ContextManager.Mcp/Program.cs` — modify; add two `AddSingleton` registrations

## Description
Add two service registrations to `Program.cs` so the DI container can resolve `ContextAnalyzer` and `CrossReferenceResolver` for constructor injection into `InspectContextTool`.

Current `Program.cs` wires the host with:
```csharp
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();
```

Add before the `AddMcpServer()` chain (or after, as a separate `builder.Services` call):
```csharp
builder.Services.AddSingleton<CrossReferenceResolver>();
builder.Services.AddSingleton<ContextAnalyzer>();
```

The `WithToolsFromAssembly()` call already discovers `InspectContextTool` via `[McpServerToolType]` — no further registration is needed for the tool itself. The MCP SDK resolves tool constructor parameters from the DI container, so `ContextAnalyzer` must be registered before the host builds.

Per `design.md §Key decision resolutions #8`: both are registered as `Singleton` because they are stateless (no per-request state, no mutable fields). This matches the existing analysis-layer pattern.

Required `using` directives: `ContextManager.Analysis` and `ContextManager.Analysis.Extraction` — add them if not already present via implicit usings.

## Acceptance
- [ ] `Program.cs` contains `AddSingleton<CrossReferenceResolver>()` and `AddSingleton<ContextAnalyzer>()`
- [ ] `dotnet build` on the full solution passes with zero errors
- [ ] `dotnet test` passes (all existing + new tests green)
- [ ] The MCP server starts without DI resolution errors (verifiable by running `dotnet run --project src/ContextManager.Mcp/ContextManager.Mcp.csproj` and confirming no startup exceptions)

## Needs tests
no

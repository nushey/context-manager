# 006 — Add InspectContextTool MCP tool

## Files
- `src/ContextManager.Mcp/Tools/InspectContextTool.cs` — create; MCP tool with input validation and delegation to ContextAnalyzer

## Description
Create `public class InspectContextTool` in namespace `ContextManager.Mcp.Tools`. Decorated with `[McpServerToolType]` — this makes `WithToolsFromAssembly()` auto-discover it. The tool method is decorated with `[McpServerTool("inspect_context")]` and parameters use `[Description("...")]` attributes for MCP metadata.

Follows the same structural pattern as `InspectFileTool`. Receives `ContextAnalyzer` via constructor injection.

**Tool method signature:**

```csharp
[McpServerTool("inspect_context"), Description("Analyze cross-file relationships in up to 15 C# source files using Roslyn semantic model.")]
public async Task<string> InspectContextAsync(
    [Description("List of absolute paths to .cs files to analyze (max 15).")] IReadOnlyList<string> filePaths,
    CancellationToken ct = default)
```

**Input validation (in this order, fail-fast):**
1. If `filePaths.Count > 15`: return `JsonSerializer.Serialize(new AnalysisError("too_many_files", $"Expected at most 15 files, got {filePaths.Count}.", null), AnalysisJson.Options)`.
2. For each path in `filePaths`: if `!File.Exists(path)`: return `JsonSerializer.Serialize(new AnalysisError("file_not_found", $"File not found: {path}", path), AnalysisJson.Options)`.

If validation passes: call `await _analyzer.AnalyzeAsync(filePaths, ct)` and return `JsonSerializer.Serialize(result, AnalysisJson.Options)`.

No try/catch wrapping — unhandled exceptions propagate to the MCP SDK (consistent with `InspectFileTool` behavior; the SDK handles protocol-level errors).

**Error codes** used: `"too_many_files"` (new), `"file_not_found"` (reused from `AnalysisError`). No changes to `AnalysisError` record needed — the existing shape supports both.

## Acceptance
- [ ] `InspectContextTool` is decorated with `[McpServerToolType]`
- [ ] Tool method is named `InspectContextAsync` and decorated with `[McpServerTool("inspect_context")]`
- [ ] Calling with `> 15` paths returns a JSON `AnalysisError` with code `"too_many_files"`
- [ ] Calling with a non-existent path returns a JSON `AnalysisError` with code `"file_not_found"` and the offending path
- [ ] Valid calls return JSON with top-level keys `files`, `references`, `unresolved`
- [ ] `dotnet build` passes

## Needs tests
no
(Validation behavior is covered indirectly by integration; unit testing MCP tool wiring requires the full MCP SDK host and is out of scope per the existing test pattern — `InspectFileTool` has no dedicated unit test either.)

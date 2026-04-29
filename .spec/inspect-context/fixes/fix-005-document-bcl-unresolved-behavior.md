# fix-005 — Document BCL-types-in-unresolved behavior in tool description

## Context files (read for understanding — do not modify)
- `src/ContextManager.Mcp/Tools/InspectContextTool.cs` — the `[Description("...")]` attribute on `InspectContextAsync` is the primary documentation surface visible to MCP clients. Currently it says only "Analyze cross-file relationships in up to 15 C# source files using Roslyn semantic model." — it says nothing about what appears in `unresolved`.
- `design.md` §"Key decision resolutions / 1. CSharpCompilation strategy" — the authoritative rationale: source-text-only compilation means BCL types (`CancellationToken`, `Task<T>`, etc.) have no `DeclaringSyntaxReferences`, so they always surface in `unresolved`. This is intentional, not a bug.
- `design.md` §"Trade-offs" — explicitly records the decision not to add BCL reference stubs.
- `src/ContextManager.Analysis/Models/ContextAnalysis.cs` — the `Unresolved` field in the output model; its XML or inline doc (if any) is a secondary documentation surface.

## Reference files (STRICT STYLE MATCH)
- `src/ContextManager.Mcp/Tools/InspectContextTool.cs` — Gold Standard for the `[Description]` attribute style: single string, no newlines, factual, written for an MCP client reading tool metadata.

## Required Skills
none

## Files to create/modify (suggested)
- `src/ContextManager.Mcp/Tools/InspectContextTool.cs` — modify (expand the `[Description]` attribute on `InspectContextAsync` to document the `unresolved` field behavior)
- `src/ContextManager.Analysis/Models/ContextAnalysis.cs` — modify (add a brief inline comment on the `Unresolved` property explaining why BCL types appear there)

## Description
The `unresolved` array in `inspect_context` output contains type names that could not be resolved to a file in the input set. This includes BCL types (`CancellationToken`, `Task<T>`, `IEnumerable<T>`, etc.) because the stub `CSharpCompilation` is built from source texts only — no assembly metadata references are loaded. An MCP client reading the output has no way to know this without documentation: it cannot tell whether `CancellationToken` in `unresolved` means "missing file" or "BCL type" without being told.

**Change 1 — `InspectContextTool`**: Expand the `[Description]` attribute string on `InspectContextAsync`. The new description must state: what the tool does, the 15-file limit, what `unresolved` contains, and that BCL / framework types (e.g. `CancellationToken`, `Task`) appear in `unresolved` by design because no external assembly metadata is loaded. Keep it as a single string under ~300 characters.

**Change 2 — `ContextAnalysis.cs`**: Add a single-line comment above (or inline on) the `Unresolved` property explaining the BCL limitation. Per AGENTS.md §"Code style": "Comments only for non-obvious *why*, never for *what*." This qualifies as a non-obvious why.

Do NOT change the behavior. Do NOT add BCL assembly stubs. Do NOT modify `design.md` architecture sections.

## Acceptance
- [ ] The `[Description]` attribute on `InspectContextAsync` mentions that types not declared in the input set (including BCL types such as `CancellationToken` or `Task`) appear in the `unresolved` array
- [ ] The description remains a single string literal (no concatenation, no newlines in source)
- [ ] `ContextAnalysis.cs` has a code comment on or above `Unresolved` explaining the BCL-types limitation
- [ ] No behavioral changes: `dotnet test` exits with code 0 with all existing tests green
- [ ] `dotnet build` exits with code 0

## Needs tests
no
(This is a documentation-only change. The behavioral fact — that BCL types appear in `unresolved` — is already asserted by `AnalyzeAsync_OrderController_CreateOrderDtoIsUnresolved` and will be additionally covered by fix-003's `via = "parameter"` test.)

---

## Implementation log (filled by dev after successful commit)
- Commit: fea0a0c — docs(inspect-context): document BCL-types-in-unresolved behavior
- Files modified:
  - src/ContextManager.Analysis/Models/ContextAnalysis.cs (modified)
  - src/ContextManager.Mcp/Tools/InspectContextTool.cs (modified)
- Tests added: none required
- Context & Reference files read:
  - src/ContextManager.Mcp/Tools/InspectContextTool.cs
  - src/ContextManager.Analysis/Models/ContextAnalysis.cs
- Notes: none

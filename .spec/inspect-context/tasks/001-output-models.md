# 001 — Add output model records for inspect_context

## Files
- `src/ContextManager.Analysis/Models/ContextTypeInfo.cs` — create; compressed type record (methods as `string[]`, no properties)
- `src/ContextManager.Analysis/Models/ReferenceInfo.cs` — create; cross-reference record (`From`, `To`, `Via`, `ResolvedFile?`)
- `src/ContextManager.Analysis/Models/ContextFileAnalysis.cs` — create; per-file record (`File`, `Namespace`, `Types[]`)
- `src/ContextManager.Analysis/Models/ContextAnalysis.cs` — create; top-level result record (`Files`, `References`, `Unresolved`)

## Description
Create the four immutable `record` types that represent the `inspect_context` JSON output contract. These are pure data shapes — no behavior, no Roslyn usage. They follow the same pattern as the existing `FileAnalysis.cs`, `TypeInfo.cs`, `MethodInfo.cs` models. See `design.md §Key decision resolutions #7 and #9` for the rationale for keeping these separate from the `inspect_file` models.

- `ContextTypeInfo`: fields `Name` (string), `Kind` (string), `Base` (string?), `Implements` (IReadOnlyList<string>?), `Attributes` (IReadOnlyList<string>?), `ConstructorDependencies` (IReadOnlyList<string>?), `Methods` (IReadOnlyList<string>?). No `Properties` field — enforced at the model level.
- `ReferenceInfo`: fields `From` (string), `To` (string), `Via` (string), `ResolvedFile` (string?).
- `ContextFileAnalysis`: fields `File` (string), `Namespace` (string?), `Types` (IReadOnlyList<ContextTypeInfo>).
- `ContextAnalysis`: fields `Files` (IReadOnlyList<ContextFileAnalysis>), `References` (IReadOnlyList<ReferenceInfo>), `Unresolved` (IReadOnlyList<string>).

All records use file-scoped namespace `ContextManager.Analysis.Models`. One public type per file; filename matches type name exactly.

## Acceptance
- [ ] Four `.cs` files exist, each containing exactly one `public sealed record` in namespace `ContextManager.Analysis.Models`
- [ ] `ContextTypeInfo` has no `Properties` field
- [ ] `ContextTypeInfo.Methods` is `IReadOnlyList<string>?` (not `IReadOnlyList<MethodInfo>?`)
- [ ] `ReferenceInfo.Via` is `string` (not an enum — validated at serialization boundary)
- [ ] `dotnet build` produces zero errors and zero warnings

## Needs tests
no

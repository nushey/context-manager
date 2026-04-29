# Design: inspect_context — Multi-File Cross-Reference Tool

## Existing conventions honored

- **Source of truth**: `AGENTS.md` (project-level), supplemented by `CLAUDE.md` (delegates to `AGENTS.md`)
- **Conventions bootstrap**: pre-existing
- **Language & framework**: C# 12 / .NET 8, Roslyn `Microsoft.CodeAnalysis.CSharp`, `ModelContextProtocol` SDK, `Microsoft.Extensions.Hosting`
- **Folder structure pattern**: Analysis logic in `src/ContextManager.Analysis/`; MCP tools in `src/ContextManager.Mcp/Tools/`; serialization records in `src/ContextManager.Mcp/Serialization/`; tests mirror source under `tests/ContextManager.Analysis.Tests/`; fixtures under `tests/ContextManager.Analysis.Tests/Fixtures/`
- **Naming conventions**: `<Concern>Extractor` for extractors; one public type per file, filename matches type; file-scoped namespaces; `record` for DTOs/output models, `class` for behavioral types
- **State / data-flow pattern**: Stateless — parse → extract → return; no intermediate files, no caching; MCP tool layer → Analysis layer (no coupling in reverse)
- **Testing setup**: MSTest; fixture-backed tests against real `.cs` files; test classes mirror source structure
- **Specific rules being honored**:
  - AGENTS.md §"Extending the extraction pipeline": new MCP tool goes in `src/ContextManager.Mcp/Tools/`, decorated with `McpServerToolType`; parameters use `[Description]`
  - AGENTS.md §"Extending the extraction pipeline": new extractor classes in `src/ContextManager.Analysis/Extraction/`, follow `<Concern>Extractor` naming
  - AGENTS.md §"Extending the extraction pipeline": DTO/output models in `src/ContextManager.Mcp/Serialization/` as `record` types with no behavior
  - AGENTS.md §"Constructor injection": new classes registered in DI container and injected, not `new`-ed inside callers
  - AGENTS.md §"Async and cancellation": all async methods return `Task`/`Task<T>` and accept `CancellationToken` as last parameter
  - AGENTS.md §"Roslyn invariants": output deterministic — declaration order preserved, never sort alphabetically; private members excluded; method bodies excluded
  - AGENTS.md §"Roslyn invariants": `CSharpCompilation` + `SemanticModel` explicitly reserved for `inspect_context` — this feature activates that upgrade path
  - AGENTS.md §"How to Add a Feature": fixture first, then test, then extractor, then wire into analyzer, then surface in MCP
  - AGENTS.md §"Conventions & Patterns / Code style": `Nullable` + `ImplicitUsings` on; `var` when RHS makes type obvious; pattern matching and LINQ over manual loops

---

## Technical approach

`ContextAnalyzer` is a new `class` in `src/ContextManager.Analysis/` that reads up to 15 `.cs` files, builds a `CSharpCompilation` from their source texts (no real assembly references), and obtains a per-file `SemanticModel`. It reuses the existing `TypeExtractor` for AST walking — but the per-type output is compressed: methods become one-line strings via a new static helper `MethodSignatureFormatter`, and properties are always omitted. A new `CrossReferenceResolver` class walks the already-extracted structural data and uses the `SemanticModel` to resolve each constructor dependency, interface, base type, and method parameter to its declaring file within the set (or flags it `unresolved`). The 15-file guard and missing-file validation live in `InspectContextTool` (the MCP boundary), consistent with how `InspectFileTool` owns its own input validation. Serialization output records for the `inspect_context` payload are new `record` types appended to `AnalysisJson.cs`'s companion file space, following the existing single-record-per-file convention.

---

## Files to create / modify

### Create

| Path | Purpose |
|------|---------|
| `src/ContextManager.Analysis/ContextAnalyzer.cs` | Orchestrates multi-file parse, builds `CSharpCompilation`, drives `CrossReferenceResolver`, returns `ContextAnalysis` |
| `src/ContextManager.Analysis/Extraction/CrossReferenceResolver.cs` | Resolves structural references (constructor deps, implements, base, method params) against the `SemanticModel`; classifies as in-set or unresolved |
| `src/ContextManager.Analysis/Extraction/MethodSignatureFormatter.cs` | Static helper that formats a `MethodInfo` as `"Name(ParamType, ...): ReturnType"` one-line string |
| `src/ContextManager.Analysis/Models/ContextAnalysis.cs` | Output model record: `Files`, `References`, `Unresolved` |
| `src/ContextManager.Analysis/Models/ContextFileAnalysis.cs` | Per-file record for `inspect_context`: `File`, `Namespace`, `Types[]` (typed as `ContextTypeInfo`) |
| `src/ContextManager.Analysis/Models/ContextTypeInfo.cs` | Compressed type record: `Name`, `Kind`, `Base?`, `Implements[]?`, `Attributes[]?`, `ConstructorDependencies[]?`, `Methods[]` as `string[]` |
| `src/ContextManager.Analysis/Models/ReferenceInfo.cs` | Cross-reference record: `From`, `To`, `Via`, `ResolvedFile?` |
| `src/ContextManager.Mcp/Tools/InspectContextTool.cs` | MCP tool class decorated with `McpServerToolType`; validates input, delegates to `ContextAnalyzer`, serializes with `AnalysisJson.Options` |
| `tests/ContextManager.Analysis.Tests/Fixtures/ContextFixtures/IOrderService.cs` | Interface fixture (declares `IOrderService`) |
| `tests/ContextManager.Analysis.Tests/Fixtures/ContextFixtures/OrderService.cs` | Service fixture (implements `IOrderService`, depends on `IOrderRepository` via constructor) |
| `tests/ContextManager.Analysis.Tests/Fixtures/ContextFixtures/IOrderRepository.cs` | Repository interface fixture |
| `tests/ContextManager.Analysis.Tests/Fixtures/ContextFixtures/OrderController.cs` | Controller fixture (depends on `IOrderService`; references `CreateOrderDto` — not in the set, produces unresolved) |
| `tests/ContextManager.Analysis.Tests/ContextAnalyzerTests.cs` | MSTest class covering: cross-reference resolution, unresolved detection, 15-file limit, missing-file error, method string compression, no properties in output, determinism |

### Modify

| Path | Purpose |
|------|---------|
| `src/ContextManager.Mcp/Program.cs` | Register `ContextAnalyzer` and `CrossReferenceResolver` in the DI container (two `AddSingleton` or `AddTransient` calls alongside existing registrations) |

> `FileAnalyzer`, `TypeExtractor`, `MemberExtractor`, `AnalysisJson`, and all existing models are **not modified**. `InspectFileTool` is not modified.

---

## Patterns / abstractions

**Reused patterns:**
- `TypeExtractor` (existing `CSharpSyntaxWalker`) is invoked as-is inside `ContextAnalyzer` to walk each file's AST — same as `FileAnalyzer` does it today. No duplication.
- `AnalysisJson.Options` (existing) is reused by `InspectContextTool` for serialization — same as `InspectFileTool`.
- `AnalysisError` (existing) is reused for error returns from `InspectContextTool` (`file_not_found`, `too_many_files`).
- `AccessLevel`, `AttributeExtractor`, `DtoDetector` continue to be used transitively through `TypeExtractor` + `MemberExtractor`.

**New abstractions and their justification:**
- `ContextAnalyzer` — new class; `FileAnalyzer` is stateless and syntax-tree-only by design (AGENTS.md §Architecture); it cannot be extended to hold a `CSharpCompilation`. A sibling orchestrator is the minimal addition.
- `CrossReferenceResolver` — new class; cross-reference logic using the `SemanticModel` is a distinct concern from type extraction; isolating it keeps `ContextAnalyzer` readable and makes the Roslyn-semantic boundary testable.
- `MethodSignatureFormatter` — new static class; the one-line method string format is `inspect_context`-specific. Putting it here avoids modifying `MethodInfo` or `MemberExtractor`, which are owned by `inspect_file`.
- `ContextTypeInfo` — new record; the compressed type shape (methods as `string[]`, no properties) is structurally different from `TypeInfo`. Reusing `TypeInfo` would require nullable hacks or post-processing at the serialization boundary — a worse trade-off than a lean sibling record.
- `ContextFileAnalysis`, `ContextAnalysis`, `ReferenceInfo` — new records; required by the output contract (scope.md §Acceptance criteria). One record per distinct JSON shape is the established pattern.

---

## Key decision resolutions

### 1. CSharpCompilation strategy
Build the compilation from source texts only — no metadata references (no BCL stubs, no NuGet assemblies). Roslyn can bind intra-set symbols without external references; types outside the set simply produce unresolved symbols, which is precisely the `unresolved` list. If Roslyn cannot resolve a type symbol it returns `null` from `GetSymbolInfo()` / `GetTypeInfo()` — the code treats `null` resolution as "unresolved" rather than an error. Parse-level diagnostics on individual files are still checked; a file that fails to parse causes the entire call to fail (consistent with `FileAnalyzer` behavior and the scope's fail-fast assumption).

### 2. SemanticModel usage — specific Roslyn APIs
For each reference node collected during AST walking:
- **Base type and implements**: `model.GetTypeInfo(baseTypeSyntax).Type` — resolves the `INamedTypeSymbol`; check `symbol.DeclaringSyntaxReferences` to find if its location maps to a file in the set.
- **Constructor dependency types**: same as above on each `ParameterSyntax.Type`.
- **Method parameter types**: same on each `ParameterSyntax.Type` in `MethodDeclarationSyntax.ParameterList`.
- **Resolved file lookup**: `symbol.DeclaringSyntaxReferences.FirstOrDefault()?.SyntaxTree.FilePath` — compare against the input file paths to produce `resolvedFile` or `null`.
- Return types are **not** tracked as references per scope.md §Assumptions ("method return types are not tracked as references in this phase").

### 3. Method compression — where the formatting lives
`MethodSignatureFormatter.Format(MethodInfo method)` — takes the existing `MethodInfo` model (already extracted by `MemberExtractor`) and produces the one-line string `"MethodName(Type1, Type2): ReturnType"`. Parameters use their type only (name omitted, matching the product plan's example). This is a pure formatting concern; it does not touch `MemberExtractor` or `MethodInfo`.

### 4. Cross-reference detection — which relationships produce `references[]` entries
Exactly four `via` values per scope.md §Acceptance criteria:
- `constructor` — each `ConstructorDependencies` parameter type
- `implements` — each entry in `Implements[]`
- `base` — the `Base` type (when present)
- `parameter` — each distinct parameter type across all non-private methods

Each produces one `ReferenceInfo` entry per `(from-type-name, to-type-name)` pair. If the same `(from, to, via)` triple appears multiple times (e.g., two methods both take `CancellationToken`), it appears once in `references` — deduplicated by `(From, To, Via)` triple, preserving first-occurrence declaration order.

### 5. 15-file limit enforcement — tool layer
Validation lives in `InspectContextTool` (MCP boundary), not in `ContextAnalyzer`. This mirrors how `InspectFileTool` owns its validation (file-not-found check, `.cs` extension check). `ContextAnalyzer` can assume its input is valid. The error returned is an `AnalysisError` record with a new code `"too_many_files"`.

### 6. Missing-file behavior
`InspectContextTool` iterates `filePaths` before calling `ContextAnalyzer`. Any path that does not exist produces an `AnalysisError("file_not_found", ..., missingPath)` and the entire call is aborted — no partial results. This is consistent with scope.md §Assumptions ("fail-fast behavior").

### 7. Output model location
New records (`ContextAnalysis`, `ContextFileAnalysis`, `ContextTypeInfo`, `ReferenceInfo`) live in `src/ContextManager.Analysis/Models/` — one file per record, matching the existing `FileAnalysis.cs`, `TypeInfo.cs`, `MethodInfo.cs` pattern. They are in the `ContextManager.Analysis.Models` namespace, accessible from both the analysis layer and the MCP layer (which already references `ContextManager.Analysis`).

### 8. DI wiring
`Program.cs` registers two new services. `WithToolsFromAssembly()` already auto-discovers `InspectContextTool` because it scans for `McpServerToolType`-decorated classes in the assembly. The two analysis classes need explicit registration:

```
services.AddSingleton<ContextAnalyzer>();
services.AddSingleton<CrossReferenceResolver>();
```

`InspectContextTool` receives `ContextAnalyzer` via constructor injection (the MCP SDK resolves tool constructor parameters from DI). `ContextAnalyzer` receives `CrossReferenceResolver` via constructor injection.

### 9. Properties in `inspect_context`
Properties are **never** included in `inspect_context` output (scope.md §Acceptance criteria, product plan §"Diferencias clave con inspect_file"). `ContextTypeInfo` has no `Properties` field at all — this is enforced at the model level, not by a runtime filter.

---

## Trade-offs

- **`ContextTypeInfo` as a separate record vs reusing `TypeInfo`**: `TypeInfo` carries `IReadOnlyList<MethodInfo>?` (objects with `StartLine`, `EndLine`, `Attributes`, etc.) and `IReadOnlyList<PropertyInfo>?`. `inspect_context` needs `IReadOnlyList<string>?` for methods and no properties field. Reusing `TypeInfo` would require either a post-serialization transform or making those fields nullable with a mode flag — both are worse than a focused sibling record. AGENTS.md permits `record` for immutable output models.
- **No compilation cache between calls**: scope.md §Out of scope explicitly defers caching. The product plan notes that cross-file compilation completes in under 3 seconds for 500 files; for ≤15 files this is imperceptible.
- **Source-text compilation without BCL references**: the alternative (adding `Basic.Reference.Assemblies` NuGet stubs for BCL types) would allow resolving `CancellationToken`, `Task<T>`, etc. as in-set or BCL symbols. But the scope assumption says external symbols surface in `unresolved`, which is the intended behavior — the agent sees `CancellationToken` as unresolved and knows it's a BCL type, not a missing file. Adding BCL stubs would be speculative complexity not required by the scope.
- **`CrossReferenceResolver` as a separate class vs inlining into `ContextAnalyzer`**: the Roslyn semantic resolution logic is non-trivial and warrants its own test surface. A separate class also keeps `ContextAnalyzer` at a single level of abstraction. Justified by AGENTS.md §"no new abstractions without justification" — this one has two call sites worth of justification (readability + testability).
- **Deduplication of `references` by `(From, To, Via)` triple**: the product plan example shows one entry per logical relationship, not one per occurrence. Deduplicating reduces noise in the output and matches the spirit of the cross-reference map as "structural relationships, not usage counts".

---

## Out of scope (technical)

- No changes to `FileAnalyzer`, `TypeExtractor`, `MemberExtractor`, `DtoDetector`, `AttributeExtractor`, `AccessLevel` — per scope.md §Out of scope.
- No changes to `InspectFileTool` or the `inspect_file` output contract.
- No partial class merging (`"partial": true` is a Phase 3 concern).
- No cross-project or cross-solution resolution.
- No BCL/NuGet package type resolution.
- No caching of `CSharpCompilation` between calls.
- Accepting type names (instead of file paths) as input is deferred per scope.md §Out of scope.
- Method return types are not tracked as cross-references (scope.md §Assumptions).
- `usings` are not included in `inspect_context` per-file output (scope.md §Assumptions).
- Attributes on methods are omitted in the compressed one-line method format (scope.md §Assumptions).

---

## Gaps for human attention

None. Conventions and scope are consistent throughout. One clarification recorded as a design decision rather than a gap: the compilation strategy (source-text only, no BCL stubs) is explicitly assumed acceptable by scope.md §Assumptions and is the simplest defensible choice.

# Scope: inspect_context — Multi-File Cross-Reference Tool (Phase 2)

## Objective

Enable AI agents to understand how a set of up to 15 C# source files relate to each other by exposing a new MCP tool that uses Roslyn's semantic model to resolve cross-type references, returning a compact structural payload with a cross-reference map and an unresolved-types list.

## User stories

- As an AI agent, I want to pass a list of C# file paths to `inspect_context` so that I receive a single JSON payload describing the structural shape of all types across those files without reading raw source.
- As an AI agent, I want the response to include a `references` array so that I can trace how types in the provided set depend on each other via constructor injection, interface implementation, base-class inheritance, and method parameters.
- As an AI agent, I want the response to include an `unresolved` list so that I know which referenced types are missing from the provided file set and can decide whether to expand my query.
- As an AI agent, I want methods represented as one-line signature strings (not objects) in the `inspect_context` output so that the payload stays compact when many files are included.
- As an AI agent, I want the tool to reject calls with more than 15 file paths so that I receive a clear error instead of an oversized or slow response.
- As an AI agent, I want the tool to use Roslyn's `SemanticModel` to resolve type references (not string matching) so that aliased types, generic types, and ambiguous names are resolved correctly.

## Acceptance criteria

- [ ] `inspect_context` is registered as an MCP tool and reachable via stdio transport under the name `inspect_context`.
- [ ] Calling the tool with a `filePaths` array of 1–15 valid `.cs` paths returns a JSON object with top-level keys `files`, `references`, and `unresolved`.
- [ ] Each entry in `files` contains: `file` (path as provided), `namespace`, and `types[]`. Each type entry includes `name`, `kind`, `base` (if present), `implements[]`, `attributes[]`, `constructorDependencies[]`, and `methods[]` as one-line strings in the format `MethodName(ParamType): ReturnType`.
- [ ] Properties are never included in `inspect_context` output, regardless of type kind.
- [ ] Each entry in `references` contains: `from` (type name), `to` (referenced type name), `via` (one of: `constructor`, `implements`, `base`, `parameter`), and `resolvedFile` (path string if the target type was found in the file set, `null` otherwise).
- [ ] Every type name that appears as a constructor dependency, interface, base type, or method parameter across all provided files and is NOT declared in the provided file set appears exactly once in `unresolved`.
- [ ] Calling the tool with more than 15 entries in `filePaths` returns an error response; no analysis is performed.
- [ ] Calling the tool with a path that does not exist on disk returns a descriptive error identifying the missing file; other valid files in the set are not silently dropped without indication.
- [ ] Cross-references are resolved using `CSharpCompilation.Create()` and per-file `SemanticModel`, not by string matching on type names.
- [ ] Output is deterministic: repeated calls with the same inputs produce identical JSON.
- [ ] Declaration order from the syntax tree is preserved in `types[]` and `methods[]`; no alphabetical sorting is applied.
- [ ] At least one multi-file MSTest fixture set exists covering: a type implementing an interface declared in another file, a type with a constructor dependency on a type declared in another file, and a type referencing a type not present in the set (producing an `unresolved` entry).
- [ ] All existing tests remain green after the feature is merged (`dotnet test` passes).

## Out of scope

- Accepting type names instead of file paths as input (noted as a potential Phase 2 UX improvement in the plan — deferred).
- Automatically discovering and merging `partial class` declarations not included in the provided file set (flag with `"partial": true` is a Phase 3 concern).
- Cross-project or cross-solution resolution — the tool never follows `<ProjectReference>` boundaries.
- Resolving types from NuGet package internals — only types declared in the provided files are resolvable.
- Caching compilation results between calls.
- Any changes to the existing `inspect_file` tool or `FileAnalyzer`.

## Assumptions

- A `CSharpCompilation` created solely from the provided source texts (without real assembly references beyond the BCL stubs needed for Roslyn to parse) is sufficient to resolve intra-set type references. External symbols that cannot be resolved will surface in `unresolved`, which is the intended behavior.
- `via` values are limited to the four enumerated cases (`constructor`, `implements`, `base`, `parameter`); method return types are not tracked as references in this phase.
- When a file path does not exist, the tool returns an error for the entire call rather than partial results, consistent with fail-fast behavior.
- `usings` are excluded from the `inspect_context` per-file output (present in `inspect_file` only) — the plan's example output does not include them, and omitting them reduces payload size at scale.
- Attributes on types are included as plain text strings (same representation as in `inspect_file`); attributes on methods are omitted in the compressed one-line method format.

## Context

Phase 1 (`inspect_file`) is complete and shipped. The existing `FileAnalyzer` / `TypeExtractor` pipeline is stateless and syntax-tree-only by design; `inspect_context` introduces the first use of `CSharpCompilation` and `SemanticModel` in the codebase, isolated to a new `ContextAnalyzer` class. The plan explicitly reserved this upgrade path. The new `InspectContextTool` follows the same `McpServerToolType` pattern as `InspectFileTool`.

---

## Audit Notes

_Appended 2026-04-29 — Quality Audit Pass. Feature status: PASS (verify.md). 122 tests green (was 111 at verify time; 11 tests added in post-verify work). The following findings do NOT block the feature — they are blind spots and inconsistencies surfaced for the next iteration._

### Finding 1 — No tests for `InspectContextTool` input-validation paths (GAP)

The verify.md incorrectly references `ContextAnalyzerTests.InspectContextTool_TooManyFiles_ReturnsError` — that test does not exist. Neither does a test for `file_not_found`. The validation logic in `InspectContextTool.cs` (lines 25–35) is exercised only manually; no automated coverage exists for the `too_many_files` or `file_not_found` error branches. These are distinct from `ContextAnalyzer` and require testing at the tool boundary.

### Finding 2 — `via = "base"` and `via = "parameter"` references have zero test coverage (GAP)

All three fixture classes use interface inheritance (IOrderService, IOrderRepository) so `via = "implements"` and `via = "constructor"` are exercised. No fixture exists with a class extending a concrete base class, so the `"base"` via path in `CrossReferenceResolver` is untested. Similarly, no test asserts that a method parameter type appears as `via = "parameter"` in the references array. The `CreateOrderDto` test only checks `unresolved`, not the corresponding `ReferenceInfo` entry.

### Finding 3 — AGENTS.md contains a stale sentence (INCONSISTENCY)

`AGENTS.md` line 34 reads: "A `CSharpCompilation` + `SemanticModel` is reserved for a future `inspect_context` tool." This is now false — `inspect_context` is implemented and shipped. Per AGENTS.md §"Keeping AGENTS.md Up to Date", the file must be regenerated via the `generate_agents_md` MCP tool, not edited manually.

### Finding 4 — Base-class heuristic inherited from `MemberExtractor` creates silent blind spot (BEHAVIOR NOTE)

`CrossReferenceResolver` derives its `via = "base"` / `via = "implements"` classification by comparing `toName` against `contextType.Base`, which was populated by `MemberExtractor`'s heuristic: the first entry in a class's BaseList is treated as `Base` only if it does not start with the letter `I`. A class named `Injectable` or `ImmutableBase` would be misclassified as `"implements"` rather than `"base"`. This is an inherited limitation of `inspect_file` that also affects `inspect_context`; it is not a regression introduced by this feature.

### Finding 5 — `unresolved` includes BCL/framework types by design — not documented in scope (UNDOCUMENTED BEHAVIOR)

The scope assumption states: "External symbols that cannot be resolved will surface in `unresolved`." In practice, `CancellationToken`, `Task<T>`, `int`, and other BCL types appear in `unresolved` because the compilation is built without metadata references. The scope's wording ("types that appear … and are NOT declared in the provided file set") implies only user types; BCL types appearing in `unresolved` may surprise callers. This is by design per the design.md §"Trade-offs" (source-text compilation without BCL stubs), but it is not called out as an observable behavior in the acceptance criteria or user stories.

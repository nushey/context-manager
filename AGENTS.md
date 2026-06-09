# AGENTS.md

## Project Overview

Context Manager is a C# / .NET 10 MCP (Model Context Protocol) server that uses Roslyn (`Microsoft.CodeAnalysis.CSharp`) to extract structural context from C# source files — and builds a directed knowledge graph of a whole solution so agents can navigate before they read. AI coding agents call it instead of reading raw source when they only need type signatures, dependencies, attributes, and relationships. The server exposes tools over stdio transport via the `ModelContextProtocol` SDK, hosted with `Microsoft.Extensions.Hosting`. Feature work is driven through Spec-Driven Development under `.spec/<feature-slug>/`.

## Tech Stack

- **Backend**: C# / .NET 10 (`net10.0` across all projects), Roslyn (`Microsoft.CodeAnalysis.CSharp.Workspaces`, `Microsoft.CodeAnalysis.Workspaces.MSBuild`), `QuikGraph` (graph engine), `ModelContextProtocol` SDK (stdio transport), `Microsoft.Extensions.Hosting` (DI/host), `Microsoft.Build.Locator` (lazy MSBuild registration for solution scans)
- **Testing**: MSTest (`Microsoft.NET.Test.Sdk`, `MSTest.TestFramework`, `MSTest.TestAdapter`)
- **Build**: `dotnet` CLI, solution file `ContextManager.sln`

## Project Map

| Module | Purpose |
|--------|---------|
| `.claude/` | Claude Code project settings and hook configuration |
| `.gemini/` | Gemini AI assistant configuration |
| `docs/` | Agent-facing docs: `AGENTS-template.md` (drop-in rules for projects that consume the MCP server) |
| `scripts/` | Auxiliary maintenance scripts |
| `nupkgs/` | Local NuGet package output directory |
| `src/ContextManager.Mcp/` | MCP server console app — entry point, tool registration, stdio host wiring |
| `src/ContextManager.Analysis/` | Roslyn analysis class library — file parsing, type extraction, knowledge graph engine, JSON serialization |
| `tests/ContextManager.Analysis.Tests/` | MSTest project covering analysis, extraction, graph, and tools; real `.cs` fixture files parsed at runtime |
| `.spec/` | SDD artifacts per feature (scope, design, tasks, verify) — gitignored, lives on disk only |

## Architecture & Data Flow

The system follows a two-layer architecture: an **MCP layer** (`ContextManager.Mcp`) that handles protocol concerns, and an **Analysis layer** (`ContextManager.Analysis`) that owns all Roslyn and graph work. MCP tools only validate input, delegate, project to contract records, and serialize — no analysis or graph logic ever lives in the MCP layer.

**Single-file flow** (`inspect_file`): an MCP client calls a tool → `InspectFileTool` (decorated with `McpServerToolType`) receives the request → delegates to `FileAnalyzer`, which orchestrates the parse pipeline → `TypeExtractor` (extends `CSharpSyntaxWalker`) walks the syntax tree and dispatches to `MemberExtractor`, `AttributeExtractor`, `AccessLevel`, and `DtoDetector` → results are serialized through `AnalysisJson` and returned as JSON.

**Multi-file flow** (`inspect_context`): `InspectContextTool` validates input (≤15 files, all paths must exist) → delegates to `ContextAnalyzer`, which reads each file, builds a `CSharpCompilation` from source texts **plus statically-cached framework metadata references** (so BCL types resolve and are excluded from `unresolved`), and reuses `TypeExtractor` for AST walking → `CrossReferenceResolver` uses the `SemanticModel` to resolve constructor dependencies, interface implementations, base types, and method parameter types to their declaring files within the set → compressed results (methods as one-line strings via `MethodSignatureFormatter`, no properties) are serialized and returned as JSON.

**Graph flow** (`project_scan` → query tools): `ProjectScanTool` lazily registers MSBuild via `MsBuildBootstrap`, then `GraphBuilder` loads the solution, runs `EdgeExtractor` per document to emit typed edges (`CONTAINS`, `IMPLEMENTS`, `INHERITS`, `INJECTS`, `CALLS`, `RETURNS`, `REFERENCES` — constructed generics collapse to their open definition), and populates `GraphStore` (QuikGraph bidirectional graph), persisted to `<solution-root>/.context-manager/graph.json` and hydratable at startup. Query tools delegate to `GraphStore`: `GraphGetDependenciesTool` → `GetAggregatedNeighbors` (member edges roll up to the declaring type, one entry per neighbor/direction with `edgeKinds` counts, self-`CONTAINS` excluded), `GraphImpactAnalysisTool` → `ImpactBackward` (transitive backward BFS with member roll-up, continuous interface bridging, inbound-`IMPLEMENTS` traversal, and a reflection-blind-spot diagnostic), `GraphPathFindTool` → `ShortestPath`.

**Architectural anchors:**
- `TypeExtractor` — the only `CSharpSyntaxWalker` subclass; owns tree traversal. All new type-visit logic goes here.
- `MemberExtractor` — builds every `TypeInfo`/`MethodInfo`/`PropertyInfo`; owns kind classification, generic name rendering (`RenderTypeName`), modifiers, accessors, events, and explicit interface implementations.
- `ConstructorParameterLocator` — the single source of truth for "which constructor parameters count as dependencies" (primary constructor first, fallback to the richest declared ctor). Consumed by `MemberExtractor`, `CrossReferenceResolver`, AND `EdgeExtractor` — never reimplement this logic inline.
- `FileAnalyzer` — single-file orchestration entry point; coordinates extractors and owns error handling.
- `ContextAnalyzer` — multi-file orchestration entry point; builds the referenced `CSharpCompilation`, drives `CrossReferenceResolver`, returns `ContextAnalysis`.
- `CrossReferenceResolver` — resolves structural references using the `SemanticModel`; in-set references get a `resolvedFile`, unknown user types land in `unresolved` (BCL/metadata types are filtered out).
- `GraphStore` — owns ALL graph queries (neighbors, impact, paths, serialization). New graph semantics go here, reusing its private helpers (`GetContainedMembers`, `IsMemberNode`, `GetDeclaringType`).
- `EdgeExtractor` — the only place edges are created; edge-granularity decisions (type-level vs member-level sources) live here.
- MCP boundaries: `InspectFileTool`, `InspectContextTool` (input validation), `ProjectScanTool`, `GraphGetDependenciesTool`, `GraphImpactAnalysisTool`, `GraphPathFindTool` — all decorated with `McpServerToolType`.

## Backend Guidelines

**Extending the extraction pipeline:**
- New extractor classes live in `src/ContextManager.Analysis/Extraction/` and follow the `<Concern>Extractor` naming pattern (shared helpers like `ConstructorParameterLocator` are the exception).
- Roslyn tree walkers MUST extend `CSharpSyntaxWalker`, not implement custom recursion.
- Graph logic lives in `src/ContextManager.Analysis/Graph/` — query semantics in `GraphStore`, edge creation in `EdgeExtractor`. MCP graph tools only project results to contracts.
- New MCP tools live in `src/ContextManager.Mcp/Tools/` and MUST be decorated with `McpServerToolType`. Parameters use `[Description("...")]` attributes for MCP metadata. Tool `[Description]` texts are part of the contract — keep them accurate when behavior changes.
- DTO/output contracts live in `src/ContextManager.Mcp/Serialization/` as `record` types with no behavior.
- New output fields on model records are additive nullable with `= null` default; empty collections become `null` (`NullIfEmpty` pattern) so JSON omits them via `WhenWritingNull`.

**Constructor injection**: analyzers, the graph stack (`GraphStore`, `GraphBuilder`, `EdgeExtractor`), and tools are wired through the host's DI container. New components are registered there and injected — not `new`-ed inside callers.

**Async and cancellation**: all `async` methods return `Task`/`Task<T>` and accept `CancellationToken` as the last parameter. Pass it through to Roslyn APIs.

**Roslyn invariants:**
- The analyzer never follows `<ProjectReference>` boundaries. Scope = the file passed in (`project_scan` is the only solution-wide operation).
- Method bodies, XML doc comments, and private members are excluded from output by design — do not add them. Two deliberate nuances: explicit interface implementations are reported with their interface-qualified name and `access: "public"` (they are reachable contract), and public events are part of the contract.
- Output JSON must be deterministic: preserve declaration order from the syntax tree (graph output preserves encounter order); never sort alphabetically.

## Conventions & Patterns

**Naming**
- One public type per file; filename matches the type name exactly.
- File-scoped namespaces (`namespace Foo.Bar;`) everywhere — no block namespaces.
- Prefer `record` for immutable/DTO types, `class` for behavioral types.

**Code style**
- C# 12 on .NET 10. `Nullable` and `ImplicitUsings` enabled project-wide — keep them on.
- Use `var` when the RHS makes the type obvious; use the explicit type otherwise.
- No XML doc comments on internal APIs. Public MCP tool handlers get them.
- Comments only for non-obvious *why*, never for *what*.
- Favor pattern matching and LINQ over manual loops when clarity wins.

**Testing**
- Test files mirror the source structure under `tests/ContextManager.Analysis.Tests/` (`Extraction/`, `Graph/`, `Tools/`, root-level analyzer tests).
- Extraction logic is tested against real `.cs` fixture files in `tests/ContextManager.Analysis.Tests/Fixtures/` (with `ContextFixtures/` and `GraphFixtures/` subfolders) — not mocked syntax trees. Graph-store tests may build stores programmatically.
- Every extractor, analyzer, or graph change requires a fixture-backed MSTest.

## How to Add a Feature

1. Create a fixture `.cs` file in `tests/ContextManager.Analysis.Tests/Fixtures/` representing the C# shape to be parsed.
2. Add a test class in `tests/ContextManager.Analysis.Tests/Extraction/<Concern>Tests.cs` using `[TestMethod]` against that fixture.
3. If adding a new extraction concern: create `<Concern>Extractor.cs` in `src/ContextManager.Analysis/Extraction/` as a plain `class` (or extend `CSharpSyntaxWalker` if tree-walking is needed).
4. Wire the new extractor into `FileAnalyzer` via constructor injection.
5. If the result should be MCP-visible: add or extend a model `record` in `src/ContextManager.Mcp/Serialization/AnalysisJson.cs` and surface it from `InspectFileTool`.
6. Verify: `dotnet test` must be green before opening a PR.

## Setup & Build Commands

```bash
dotnet restore
dotnet build
dotnet run --project src/ContextManager.Mcp/ContextManager.Mcp.csproj   # stdio MCP server
```

## Testing

- **Framework**: MSTest
- **Run all tests**: `dotnet test`
- **Single project**: `dotnet test tests/ContextManager.Analysis.Tests/ContextManager.Analysis.Tests.csproj`
- **Filter by name**: `dotnet test --filter "FullyQualifiedName~TypeExtractor"`
- Fixtures are real `.cs` files under `tests/ContextManager.Analysis.Tests/Fixtures/` parsed at test runtime — never mocked.

## Pull Request Guidelines

- **Conventional Commits**: `feat:`, `fix:`, `chore:`, `refactor:`, `test:`, `docs:` — subject ≤72 chars.
- **Never** add `Co-Authored-By` or AI attribution lines to commits.
- **Never** skip hooks (`--no-verify`, `--no-gpg-sign`). Fix the root cause if a hook fails.
- PR title: `[component] short description` (e.g. `[analysis] add DTO detector`).
- `dotnet build` and `dotnet test` must both be green before opening a PR.
- One feature = one branch = one PR. Branch naming: `feature/<slug>` matching the SDD slug.

## Working with SDD

Feature work flows through `.spec/<slug>/` in five phases: scope → design → tasks → implement → verify. Each phase has a dedicated subagent. The Orchestrator never writes production code.

- One task file = one atomic commit on the feature branch.
- The Verifier is the only actor that pushes the branch and opens the PR. Humans merge.
- `scope.md` and `design.md` are authoritative — update them if implementation drifts, never silently diverge.

## Keeping AGENTS.md Up to Date

This file is generated and maintained by the `agents-md-generator` MCP tool.
**Never edit it manually.** To regenerate after code changes, ask your AI assistant:

> "Update the AGENTS.md for this project"

The assistant will invoke the `generate_agents_md` tool automatically, perform an
incremental scan of changed files, and rewrite only the affected sections.
To force a full rescan from scratch: "Regenerate the AGENTS.md from scratch".

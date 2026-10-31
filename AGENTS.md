# AGENTS.md

## Project Overview

Context Manager is a C# / .NET 10 MCP (Model Context Protocol) server that uses Roslyn (`Microsoft.CodeAnalysis.CSharp`) to extract structural context from C# source files — and builds a directed knowledge graph of a whole solution so agents can navigate before they read. AI coding agents call it instead of reading raw source when they only need type signatures, dependencies, attributes, and relationships. The server exposes tools over stdio transport via the `ModelContextProtocol` SDK, hosted with `Microsoft.Extensions.Hosting`. Feature work follows Spec-Driven Development under `.spec/<feature-slug>/`.

## Tech Stack

- **Backend**: C# / .NET 10 (`net10.0` across all projects), Roslyn (`Microsoft.CodeAnalysis.CSharp.Workspaces`, `Microsoft.CodeAnalysis.Workspaces.MSBuild`), `ModelContextProtocol` SDK (stdio transport), `Microsoft.Extensions.Hosting` (DI/host), `Microsoft.Build.Locator` (lazy MSBuild registration for solution scans), `Microsoft.Build` (compile-only; in-process evaluation of graph freshness inputs), `Spectre.Console` (setup wizard prompts), `Tomlyn` (TOML parsing for the setup wizard)
- **Testing**: MSTest (`Microsoft.NET.Test.Sdk`, `MSTest.TestFramework`, `MSTest.TestAdapter`)
- **Build**: `dotnet` CLI, solution file `ContextManager.sln`

## Project Map

| Module | Purpose |
|--------|---------|
| `.claude/` | Claude Code project settings and hook configuration |
| `.gemini/` | Gemini AI assistant configuration |
| `docs/` | Agent-facing docs: `AGENTS-template.md` (drop-in rules for projects that consume the MCP server) |
| `scripts/` | Auxiliary maintenance scripts and the one-line installers (`install.sh`, `install.ps1`) |
| `nupkgs/` | Local NuGet package output directory |
| `src/ContextManager.Mcp/` | MCP server console app — entry point, tool registration, stdio host wiring, `setup` subcommand (`Setup/`) |
| `src/ContextManager.Analysis/` | Roslyn analysis class library — file parsing, type extraction, knowledge graph engine, JSON serialization |
| `tests/ContextManager.Analysis.Tests/` | MSTest project covering analysis, extraction, graph, tools, and setup; real fixture files loaded at runtime |
| `.spec/` | SDD artifacts per feature (scope, design, tasks, verify) — gitignored, lives on disk only |

## Architecture & Data Flow

The system follows a two-layer architecture: an **MCP layer** (`ContextManager.Mcp`) that handles protocol concerns, and an **Analysis layer** (`ContextManager.Analysis`) that owns all Roslyn and graph work. MCP tools only validate input, delegate, project results to contract records, and serialize; analysis and graph logic never live in the MCP layer.

**Single-file flow** (`inspect_file`): an MCP client calls a tool → `InspectFileTool` (decorated with `McpServerToolType`) receives the request → delegates to `FileAnalyzer`, which orchestrates the parse pipeline → `TypeExtractor` (extends `CSharpSyntaxWalker`) walks the syntax tree and dispatches to `MemberExtractor`, `AttributeExtractor`, and `AccessLevel`; the file is compiled alone with the framework references (`FrameworkCompilation`) so base class vs interface is classified by `TypeKind` (the `I`-prefix rule is only the fallback for unresolved types) → `AnalysisJson` serializes the results to JSON.

**Multi-file flow** (`inspect_context`): `InspectContextTool` validates input (≤15 files, all paths must exist) → delegates to `ContextAnalyzer`, which reads each file, builds a `CSharpCompilation` through `FrameworkCompilation` (source texts **plus statically-cached framework metadata references** from the runtime and every installed shared framework of the same version, so BCL and ASP.NET Core types resolve and are excluded from `unresolved`), and reuses `TypeExtractor` for AST walking → `CrossReferenceResolver` uses the `SemanticModel` to resolve constructor dependencies, interface implementations, base types, and method parameter types to their declaring files within the set → the tool serializes compressed results (methods as one-line strings via `MethodSignatureFormatter`, no properties) to JSON. Inspections never read, build, or synchronize a graph.

**Graph flow** (`project_scan` and query tools → `IGraphSynchronizer`): `project_scan` and the three query tools all delegate to `GraphSynchronizer`, which holds the solution's writer lock from `GraphRegistry`, loads `<solution-root>/.context-manager/<solution name>.graph.json` (one file per `.sln`) through `GraphRegistry.TryLoad` (reloaded whenever its last-write time changes, so every session or agent working on the solution sees the latest graph with no configuration), and brings it up to date before the query runs:
- **Freshness** (no MSBuild): `GraphWorkspaceSession.CheckFreshness` compares the persisted baseline with disk by SHA-256 — every evaluated document and additional file, the candidate `.cs` set under project directories and the fixed directories of evaluated `Compile` globs (obj/bin excluded), and evaluation inputs (`.sln`, project files, `.props`/`.targets`/`global.json` in project and ancestor directories, `obj/project.assets.json`, every effective Roslyn analyzer configuration, `.editorconfig`/`*.globalconfig` membership in project and source-file ancestor directories, and every import of the in-process MSBuild evaluation that `GraphEvaluationInputs` records on the cold path). No difference → no extraction and no rewrite. Environment-dependent evaluation is not detected; `project_scan` always forces re-evaluation.
- **Warm session** (only document text changed and the live `GraphWorkspaceSession` matches the graph revision): changed texts are applied to the live Roslyn `Solution`; a project whose `GetDependentSemanticVersionAsync` or source-generated output changed is fully re-extracted (generator changes also invalidate projects that transitively depend on it), otherwise only its changed documents are.
- **Cold path** (evaluation inputs or membership changed, missing/replaced baseline, or `project_scan`): the solution is re-opened with `MSBuildWorkspace`; projects whose evaluated fingerprint, references, documents, or additional files differ from the baseline, plus their reverse dependency closure in the old and new graphs, are fully re-extracted. Unrelated projects reuse their persisted contributions. A missing graph, or a file without per-document contributions and baseline (loaded as an empty graph), gets one full scan.
- **Extraction and publication**: `GraphBuilder` extracts the selected documents into `GraphDocumentRecord` contributions (`EdgeExtractor` returns each document's declared nodes and its typed edges `CONTAINS`, `IMPLEMENTS`, `INHERITS`, `INJECTS`, `CALLS`, `RETURNS`, `REFERENCES`; constructed generics collapse to their open definition; local functions are never nodes). `GraphStore.Stage` rebuilds an unpublished candidate from the updated document set; `GraphRegistry.SaveAsync` writes it atomically (temporary file + replace) under the per-solution writer lock, then publishes it. Concurrent processes may overwrite each other's file; the next synchronization reconciles it with disk before answering. Failed, empty, or cancelled synchronizations keep the previous graph; query tools return the same errors as `project_scan`.

Query tools then read the returned snapshot: `GraphGetDependenciesTool` → `GetAggregatedNeighbors` (member edges roll up to the declaring type, one entry per neighbor/direction with `edgeKinds` counts and the neighbor's `file`, self-`CONTAINS` excluded), `GraphImpactAnalysisTool` → `ImpactBackward` (transitive backward BFS with member roll-up, continuous interface bridging, inbound-`IMPLEMENTS` traversal, and `affectedFiles` for the returned IDs), `GraphPathFindTool` → `ShortestPath`. `graph_not_found` is returned only when neither the `.sln` nor its graph file exists.

**Setup flow** (`context-manager setup [-y]`): `Program.cs` dispatches `setup` before building the host (same pattern as `--version` / `-v`) → `SetupCommand` gets the clients for the current OS from `SetupCatalog` (paths resolved from the injected `SetupEnvironment` roots: home, `%APPDATA%`, `XDG_CONFIG_HOME`, OS) → interactive multi-select via Spectre.Console, or every client with `-y` (no TTY and no `-y` → exit 1) → `JsonConfigInserter` (surgical insertion into the root-level `mcpServers`/`mcp` object located with `Utf8JsonReader`, validated with `JsonDocument`) or `TomlConfigAppender` (Codex; appends `[mcp_servers.context-manager]`, checked and validated with Tomlyn) → `configured / skipped / failed` summary and `PATH` warning. `scripts/install.sh` and `scripts/install.ps1` are thin bootstraps: they check for a .NET 10+ SDK, install or update the global tool, and run `~/.dotnet/tools/context-manager setup`.

**Architectural anchors:**
- `TypeExtractor` — the only `CSharpSyntaxWalker` subclass; owns tree traversal. All new type-visit logic goes here.
- `MemberExtractor` — builds every `TypeInfo`/`MethodInfo`/`PropertyInfo`; owns kind classification, generic name rendering (`RenderTypeName`), modifiers, accessors, events, and explicit interface implementations.
- `ConstructorParameterLocator` — the single source of truth for "which constructor parameters count as dependencies" (primary constructor first, fallback to the richest declared ctor). `MemberExtractor`, `CrossReferenceResolver`, and `EdgeExtractor` all consume it; never reimplement this logic inline.
- `FileAnalyzer` — single-file orchestration entry point; coordinates extractors and owns error handling.
- `ContextAnalyzer` — multi-file orchestration entry point; builds the referenced `CSharpCompilation`, drives `CrossReferenceResolver`, returns `ContextAnalysis`.
- `CrossReferenceResolver` — resolves structural references using the `SemanticModel`; in-set references get a `resolvedFile`, unknown user types land in `unresolved`, and BCL/metadata types are filtered out.
- `GraphStore` — owns ALL graph queries (neighbors, impact, paths, serialization). Each snapshot is immutable and rebuilt in one pass from the `GraphDocumentRecord`s in project and document order: the first declaration of a node wins over references, and edges keep first-encounter order, so an incremental update matches a fresh scan. `Stage` prepares a candidate without publishing, `Publish` swaps it in, `Capture` gives a tool one consistent revision. New graph semantics go here, reusing its private helpers (`GetContainedMembers`, `IsMemberNode`, `GetDeclaringTypeId`).
- `GraphRegistry` — one `GraphStore` per graph file; the single source of truth for the graph file location (`GetGraphPath`), loading/reloading it by last-write time, the per-solution writer lock, and atomic persistence (`SaveAsync`). Never inject a bare `GraphStore` into tools.
- `GraphSynchronizer` (`IGraphSynchronizer`) — the only synchronization algorithm; `project_scan` and the graph query tools both delegate to it. It owns the per-solution `GraphWorkspaceSession`s.
- `GraphWorkspaceSession` — owns a live `MSBuildWorkspace`/`Solution` baseline and decides what is dirty: freshness check, warm semantic-version/generator comparison, cold fingerprint diff with reverse dependency closure.
- `GraphBuilder` — extracts selected documents of an evaluated project into `GraphDocumentRecord`s; it holds no workspace or session state.
- `EdgeExtractor` — the only place edges are created; edge-granularity decisions (type-level vs member-level sources) live here.
- `SetupCatalog` — the single source of truth for supported clients, their per-OS config paths, and their entries. Setup code lives in `src/ContextManager.Mcp/Setup/` and never touches the analysis layer.
- MCP boundaries: `InspectFileTool`, `InspectContextTool` (input validation), `ProjectScanTool`, `GraphGetDependenciesTool`, `GraphImpactAnalysisTool`, `GraphPathFindTool` — all decorated with `McpServerToolType`. `GraphSyncRunner` (internal) maps synchronizer outcomes to the shared scan errors and coverage summary for all of them. Every graph tool takes only `IGraphSynchronizer`.

## Backend Guidelines

**Extending the extraction pipeline:**
- New extractor classes live in `src/ContextManager.Analysis/Extraction/` and follow the `<Concern>Extractor` naming pattern (shared helpers like `ConstructorParameterLocator` are the exception).
- Roslyn tree walkers MUST extend `CSharpSyntaxWalker`, not implement custom recursion.
- Graph logic lives in `src/ContextManager.Analysis/Graph/` — query semantics and contribution indexes in `GraphStore`, per-solution store lookup, graph file loading and persistence in `GraphRegistry`, synchronization in `GraphSynchronizer`/`GraphWorkspaceSession`, document extraction in `GraphBuilder`, edge creation in `EdgeExtractor`. MCP graph tools only project results to contracts.
- New MCP tools live in `src/ContextManager.Mcp/Tools/` and MUST be decorated with `McpServerToolType`. Parameters use `[Description("...")]` attributes for MCP metadata. Tool `[Description]` texts are part of the contract — keep them accurate when behavior changes.
- DTO/output contracts live in `src/ContextManager.Mcp/Serialization/` as `record` types with no behavior.
- New output fields on model records are additive and nullable, with a `= null` default; empty collections become `null` (the `NullIfEmpty` pattern), so `WhenWritingNull` omits them from the JSON.

**Constructor injection**: the host's DI container wires analyzers, the graph stack (`GraphRegistry`, `GraphBuilder`, `EdgeExtractor`, `IGraphSynchronizer`), and tools. MSBuild registration stays in the MCP layer: `GraphSynchronizer` receives `MsBuildBootstrap.EnsureRegistered` and calls it lazily, only before it creates a workspace. Register new components there and inject them; never `new` them inside callers.

**Async and cancellation**: all `async` methods return `Task`/`Task<T>` and accept `CancellationToken` as the last parameter. Pass it through to Roslyn APIs.

**Roslyn invariants:**
- The analyzer never follows `<ProjectReference>` boundaries. Its scope is the file passed in; `project_scan` is the only solution-wide operation.
- The output excludes method bodies, XML doc comments, and private members by design; do not add them. Two deliberate nuances: explicit interface implementations are reported with their interface-qualified name and `access: "public"` (they are reachable contract), and public events are part of the contract.
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
- Test files mirror the source structure under `tests/ContextManager.Analysis.Tests/` (`Extraction/`, `Graph/`, `Tools/`, `Setup/`, root-level analyzer tests).
- Extraction tests run against real `.cs` fixture files in `tests/ContextManager.Analysis.Tests/Fixtures/` (with `ContextFixtures/` and `GraphFixtures/` subfolders), not mocked syntax trees; `MemberExtractor` unit tests may parse inline snippets. Graph-store and graph-tool tests build stores programmatically from document records (`Graph/TestGraph.cs`). Setup tests use real config files from `Fixtures/SetupFixtures/` copied into a temp home.
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
dotnet run --project src/ContextManager.Mcp/ContextManager.Mcp.csproj -- --version   # print CLI version
dotnet run --project src/ContextManager.Mcp/ContextManager.Mcp.csproj -- setup [-y]   # client setup wizard
```

## Testing

- **Framework**: MSTest
- **Run all tests**: `dotnet test`
- **Single project**: `dotnet test tests/ContextManager.Analysis.Tests/ContextManager.Analysis.Tests.csproj`
- **Filter by name**: `dotnet test --filter "FullyQualifiedName~TypeExtractor"`
- Fixtures are real files under `tests/ContextManager.Analysis.Tests/Fixtures/` (`.cs` sources, plus config files in `SetupFixtures/`), loaded at test runtime and never mocked.

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

This file is maintained by hand. Do not regenerate it with `agents-md-generator` or any other
generator. When a change alters the architecture, anchors, conventions, or commands described here,
update only the affected sections in the same change.

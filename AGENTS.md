# AGENTS.md

## Project Overview

Context Manager is a C# MCP (Model Context Protocol) server that uses Roslyn to extract structural context from C# projects. AI coding agents call it instead of reading raw source when they only need type signatures, dependencies, attributes, and relationships.

The full product plan lives in `context-manager-csharp-final.md` at the repo root (Spanish). Feature work is driven through Spec-Driven Development under `.spec/<feature-slug>/`.

### Stack

- **.NET 8 LTS** — target framework across all projects.
- **Roslyn** (`Microsoft.CodeAnalysis.CSharp`) — syntax + semantic analysis engine.
- **MCP SDK** (`ModelContextProtocol`) — stdio transport, tool registration.
- **Microsoft.Extensions.Hosting** — host/DI for the MCP server.

### Repo layout (current)

```
ContextManager.sln
src/
  ContextManager.Mcp/              # MCP server console app entry point
    ContextManager.Mcp.csproj
    Program.cs
  ContextManager.Analysis/         # Roslyn analysis class library
    ContextManager.Analysis.csproj
tests/
  ContextManager.Analysis.Tests/   # MSTest test project
    ContextManager.Analysis.Tests.csproj
    Fixtures/                      # Real .cs files parsed at test runtime
context-manager-csharp-final.md    # full product plan
.spec/<feature-slug>/              # SDD artifacts per feature
```

## Setup Commands

- Restore: `dotnet restore`
- Build: `dotnet build`
- Run the MCP server (stdio): `dotnet run --project src/ContextManager.Mcp/ContextManager.Mcp.csproj`

## Testing

- Test framework: **MSTest**.
- Run all tests: `dotnet test`
- Run a single test project: `dotnet test tests/<Project>.Tests/<Project>.Tests.csproj`
- Filter by name: `dotnet test --filter "FullyQualifiedName~OrderService"`
- Test fixtures live next to the test project under `Fixtures/` as real `.cs` files that the analyzer parses.
- Every analyzer/extractor change needs a fixture-backed unit test.

## Code Style

- C# 12 on .NET 8. `Nullable` and `ImplicitUsings` are enabled at the project level; keep them on.
- File-scoped namespaces (`namespace Foo.Bar;`) everywhere. No block namespaces.
- One public type per file; filename matches the type.
- Prefer `record` for immutable data / DTO-like models, `class` for behavior.
- `async` methods return `Task` / `Task<T>` and take `CancellationToken` at the end of the parameter list.
- Use `var` when the right-hand side makes the type obvious; otherwise use the explicit type.
- No XML doc comments on internal APIs. Public/exported APIs (MCP tool handlers, NuGet-exposed types) get them.
- Do not add a comment that only restates the code. Comment the non-obvious why.
- Favor pattern matching and LINQ over manual loops when clarity wins.
- Roslyn visitors: prefer `CSharpSyntaxWalker` subclasses over hand-rolled recursion.

## Build and Output

- `dotnet build -c Release` produces binaries under `**/bin/Release/net8.0/`.
- No packaging, publishing, or `dotnet tool install` steps in scope for the MVP.

## Pull Request Guidelines

- **Conventional Commits** for all commit messages (`feat:`, `fix:`, `chore:`, `refactor:`, `test:`, `docs:`). Keep the subject ≤72 chars.
- **Never** add `Co-Authored-By` or AI attribution lines to commits.
- **Never** skip hooks (`--no-verify`, `--no-gpg-sign`). If a hook fails, fix the root cause.
- PR title format: `[component] short description` (e.g. `[analysis] add DTO detector`).
- Before opening a PR: `dotnet build` and `dotnet test` must both be green locally. The Verifier enforces this.
- One feature = one branch = one PR. Branch naming: `feature/<slug>` matching the SDD feature slug.

## Working with SDD

This project uses the `sdd-flow` plugin. Feature work flows through `.spec/<slug>/` in five phases: scope → design → tasks → implement → verify. Each phase has a dedicated subagent. The Orchestrator never writes production code.

- One task file = one atomic commit on the feature branch.
- The Verifier is the only actor that pushes the branch and opens the PR. Humans merge.
- `scope.md` and `design.md` are authoritative; if the implementation drifts, update them rather than silently diverging.

## Additional Notes

- Roslyn parses a single file in milliseconds. There is **no caching layer** in this project by design — stay stateless.
- The analyzer never follows `<ProjectReference>` boundaries. Project root = nearest ancestor `.csproj`.
- The MVP uses syntax trees only. A `CSharpCompilation` + `SemanticModel` is reserved for the future `inspect_context` tool.
- Method bodies, XML doc comments, and private members are intentionally excluded from tool output. If you are tempted to include them, re-read the extraction rules in the plan.
- Output JSON must be deterministic for identical input on the same machine. Preserve declaration order from the syntax tree; do not sort alphabetically.

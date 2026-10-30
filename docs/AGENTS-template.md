## Context Manager MCP

**MANDATORY**: Call these tools before reading or editing any C# file. They are read-only and stateless.

### Rules

| Situation | Tool | Input |
|-----------|------|-------|
| Touching a single file you haven't read | `inspect_file` | `{ "filePath": "/abs/path/File.cs" }` |
| Task spans multiple files | `inspect_context` | `{ "filePaths": ["/abs/path/A.cs", "/abs/path/B.cs"] }` |

- **Always use absolute paths.**
- **`inspect_context` max 15 files.** Split into smaller batches if needed.
- **Check `unresolved`** — types listed there are dependencies not in your file set. Add their files and call again; when `unresolvedHints` is present, take the files from its `{ type, id, file }` entries (every matching graph type is listed, so pick the right `id`).
- **Read `graph` on each `inspect_file` type** when present: `nodeId` is the exact ID for graph tools, `inboundCount`/`outboundCount` size its neighborhood, and `directCallers`/`directDependencies` list up to 5 neighbors with their `file`. It reflects the last persisted graph; `inspect_file` never updates it. When the solution has no graph yet, `graphStatus` is `not_initialized (run project_scan on <sln>)`.
- **Do not call these tools for non-`.cs` files** — they will fail.
- **Do not use these tools as a substitute for reading method bodies.** Use `read_file` with line numbers from the output when you need logic.

### Knowledge Graph

Use graph tools to navigate before you read. The starting point is always the file the user mentions. Every graph tool takes `solutionPath`: the absolute path of the solution's `.sln`. Before answering, graph tools build the graph when it is missing and re-extract only what changed since the last scan. Run `project_scan` to force an MSBuild re-evaluation (for example after environment-dependent build changes). If a graph tool returns `graph_not_found`, neither the `.sln` nor its graph exists: check `solutionPath`. Scan errors (`scan_failed`, `scan_incomplete`, `scan_empty`, `scan_cancelled`, `msbuild_not_found`) mean the graph could not be updated.

| Step | Tool | Purpose |
|------|------|---------|
| 1 | `inspect_file(path)` | Read the type → note `graph.nodeId` (or namespace and type name) |
| 2 | `graph_get_dependencies(solutionPath, nodeId)` | Discover adjacent files worth reading |
| 3 | `inspect_file` on the `file` of relevant neighbors | Read only what you actually need |
| 4 | `graph_impact_analysis(solutionPath, nodeId)` | Assess blast radius before changing anything |

**Reading `graph_get_dependencies` results:** entries are aggregated per neighbor type and direction. `direction: "in"` = the neighbor depends on the queried node; `direction: "out"` = the queried node depends on the neighbor. `edgeKinds` counts the edges per kind (`INJECTS`, `CALLS`, `REFERENCES`, `IMPLEMENTS`, …). `file` is the neighbor's declaring source file. `graph_impact_analysis` maps each returned `affectedIds` entry to its file in `affectedFiles`. The queried type's own members are never listed — use `inspect_file` for those.

**`graph_impact_analysis` is a risk calibration tool — not a verification checklist.**
- Few results → change is contained, proceed normally.
- Many results → this type's public contract is load-bearing. Preserve it.
- Do NOT inspect every node in the result. Use the count and direct callers to decide how conservative to be.

**`graph_path_find(solutionPath, sourceId, targetId)`** is available for investigation: use it when you need to understand why two nodes are connected or trace the dependency chain between two specific types.

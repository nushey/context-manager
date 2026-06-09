## Context Manager MCP

**MANDATORY**: Call these tools before reading or editing any C# file. They are read-only and stateless.

### Rules

| Situation | Tool | Input |
|-----------|------|-------|
| Touching a single file you haven't read | `inspect_file` | `{ "filePath": "/abs/path/File.cs" }` |
| Task spans multiple files | `inspect_context` | `{ "filePaths": ["/abs/path/A.cs", "/abs/path/B.cs"] }` |

- **Always use absolute paths.**
- **`inspect_context` max 15 files.** Split into smaller batches if needed.
- **Check `unresolved`** — types listed there are dependencies not in your file set. Add their files and call again.
- **Do not call these tools for non-`.cs` files** — they will fail.
- **Do not use these tools as a substitute for reading method bodies.** Use `read_file` with line numbers from the output when you need logic.

### Knowledge Graph (if `project_scan` has been run)

Use graph tools to navigate before you read. The starting point is always the file the user mentions.

| Step | Tool | Purpose |
|------|------|---------|
| 1 | `inspect_file(path)` | Read the type → note namespace and type name |
| 2 | `graph_get_dependencies(namespace.TypeName)` | Discover adjacent files worth reading |
| 3 | `inspect_file` on relevant neighbors | Read only what you actually need |
| 4 | `graph_impact_analysis(namespace.TypeName)` | Assess blast radius before changing anything |

**Reading `graph_get_dependencies` results:** entries are aggregated per neighbor type and direction. `direction: "in"` = the neighbor depends on the queried node; `direction: "out"` = the queried node depends on the neighbor. `edgeKinds` counts the edges per kind (`INJECTS`, `CALLS`, `REFERENCES`, `IMPLEMENTS`, …). The queried type's own members are never listed — use `inspect_file` for those.

**`graph_impact_analysis` is a risk calibration tool — not a verification checklist.**
- Few results → change is contained, proceed normally.
- Many results → this type's public contract is load-bearing. Preserve it.
- Do NOT inspect every node in the result. Use the count and direct callers to decide how conservative to be.

**`graph_path_find(sourceId, targetId)`** is available for investigation: use it when you need to understand why two nodes are connected or trace the dependency chain between two specific types.

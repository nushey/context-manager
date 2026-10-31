## Context Manager MCP

This repository has a prebuilt knowledge graph of its C# code. Queries answer in well under a second. Every tool is read-only and stateless.

**Your first step on any question about C# code must be a Context Manager call, not `grep`, `rg`, `sed`, `cat` or a file read.** Text search misses usages through `var` and interfaces, and it confuses overloads and methods with the same name.

| Question | Call first |
|----------|------------|
| Who calls, implements, injects or uses a type or method? | `graph_get_dependencies` on that type or method |
| How does one type or method reach another? | `graph_path_find` between them |
| What does a type expose and depend on? | `inspect_file` on its file |
| What does a set of files have in common? | `inspect_context` on those files |

- Method node IDs include parameter types, for example `MyApp.Services.IOrderService.GetOrderAsync(int)`. Query the type first; its result lists its neighbors' files.
- Use `grep` or file reads only afterwards, to read method bodies at the files and line ranges the tools returned, or for non-C# files and string literals.

### Inspect files

| Situation | Tool | Input |
|-----------|------|-------|
| You will touch one `.cs` file you haven't read | `inspect_file` | `{ "filePath": "/abs/path/File.cs" }` |
| The task spans several `.cs` files | `inspect_context` | `{ "filePaths": ["/abs/path/A.cs", "/abs/path/B.cs"] }` |

- Pass absolute paths.
- Pass at most 15 files to `inspect_context`; split larger sets into batches.
- Check `unresolved` in the `inspect_context` result: it lists dependencies outside your file set. Add their files and call again.
- Pass only `.cs` files; other files fail.
- Do not use these tools in place of method bodies. When you need logic, read the file with the method's line range from the output.

### Navigate the knowledge graph

Use the graph tools to find what to read before you read it. Start from the file the user mentions.

- Every graph tool takes `solutionPath`: the absolute path of the solution's `.sln`.
- A node ID is the symbol's fully qualified name, for example `MyApp.Services.OrderService`.
- Before answering, a graph tool builds the graph if it is missing and re-extracts only what changed since the last scan. Run `project_scan` only to force an MSBuild re-evaluation, for example after environment-dependent build changes.
- `graph_not_found` means neither the `.sln` nor its graph exists: check `solutionPath`.
- `scan_failed`, `scan_incomplete`, `scan_empty`, `scan_cancelled` and `msbuild_not_found` mean the graph could not be updated.

| Step | Tool | Purpose |
|------|------|---------|
| 1 | `inspect_file(path)` | Read the type; note its namespace and type name, which form its node ID |
| 2 | `graph_get_dependencies(solutionPath, nodeId)` | Find adjacent files worth reading |
| 3 | `inspect_file` on the `file` of relevant neighbors | Read only what you need |

**`graph_get_dependencies` results** have one entry per neighbor type and direction:

- `direction: "in"`: the neighbor depends on the queried node.
- `direction: "out"`: the queried node depends on the neighbor.
- `edgeKinds`: edge counts per kind (`INJECTS`, `CALLS`, `REFERENCES`, `IMPLEMENTS`, …).
- `file`: the neighbor's declaring source file.

The queried type's own members are never listed; use `inspect_file` for those.

**`graph_path_find(solutionPath, sourceId, targetId)`**: use it to understand why two nodes are connected or to trace the dependency chain between two specific types.

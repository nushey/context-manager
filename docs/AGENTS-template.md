## Context Manager MCP

Every tool is read-only and stateless.

**Your first step on any question about C# code must be a Context Manager call, not `grep`, `rg`, `sed`, `cat` or a file read.** Text search misses usages through `var` and interfaces, and it confuses overloads and methods with the same name.

| Question | Call first |
|----------|------------|
| Where are files or methods with possible names, when no path or node ID is known? | `graph_search(solutionPath, queries)` with several candidate names |
| Who calls, implements, injects or uses a type or method? | `graph_get_dependencies` on that type or method |
| What depends on a type or method, directly or transitively, before I change it? | `graph_impact_analysis` on that type or method |
| How does one type or method reach another? | `graph_path_find` between them |
| What does a type expose and depend on? | `inspect_file` on its file |
| How do several files reference each other? | `inspect_context` on those files |

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

Use the graph tools to find what to read before you read it. When no path or node ID is known,
call `graph_search(solutionPath, queries)` with possible file or method names, for example
`["Login", "Authentication", "SignIn"]`. Continue with `inspect_file` on a returned `file` or
queries using a method's unchanged `nodeId`. Otherwise start from the file the user mentions.

`graph_search` matches any term by ordinal case-insensitive fragment against file names without
`.cs` and method names without signatures; constructors match their type name. Namespaces are output
information, not search targets; directory paths, parameter types and bodies are not searched. Terms
are trimmed and each must have at least 3 characters. An empty list or one shorter term rejects the
entire query before synchronization with `invalid_query` and
`Provide at least one query term; each term must be at least 3 characters long after trimming whitespace.`

The tool searches all Method nodes already in the graph, including private methods. This does not
change the inspection tools' visibility rules. It selects exact matches first, then partial matches,
in graph encounter order, without boosting matches to multiple terms. The limit is 50 unique
file/method matches before grouping: one per matching file and one per matching method ID.

The compact one-line response has `results` and `truncated`. Each file entry always has `file`,
`namespaces`, `fileMatched`, and `methods`; each method has `name` and `nodeId`. Arrays remain present
even when empty. `fileMatched` describes a file match retained within the limit; a file may appear
through a selected method while its own matching name was omitted. Namespaces describe the whole
file. No matches returns `{"results":[],"truncated":false}`. `truncated:true` means additional unique
matches were omitted; there is no pagination. Narrow the terms when needed.

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
| 4 | `graph_impact_analysis(solutionPath, nodeId)` | Assess the blast radius before changing anything |

**`graph_get_dependencies` results** have one entry per neighbor type and direction:

- `direction: "in"`: the neighbor depends on the queried node.
- `direction: "out"`: the queried node depends on the neighbor.
- `edgeKinds`: edge counts per kind (`INJECTS`, `CALLS`, `REFERENCES`, `IMPLEMENTS`, …).
- `file`: the neighbor's declaring source file.

The queried type's own members are never listed; use `inspect_file` for those.

**`graph_impact_analysis`** returns `affectedIds` and maps each one to its file in `affectedFiles`. Use it to calibrate risk, not as a checklist to verify:

- Few results: the change is contained; proceed normally.
- Many results: the type's public contract is load-bearing; preserve it.
- Do not inspect every returned node. Decide how conservative to be from the count and the direct callers.

**`graph_path_find(solutionPath, sourceId, targetId)`**: use it to understand why two nodes are connected or to trace the dependency chain between two specific types.

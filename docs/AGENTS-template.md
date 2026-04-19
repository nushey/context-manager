## Context Manager MCP

Use the `context-manager` MCP tools to understand C# file structure before reading or editing code. Both tools are read-only and stateless.

### When to call `inspect_file`

Call it when you need to understand a single file — its types, public API surface, constructor dependencies, and method signatures — without reading the full source.

**Use it before:**
- Editing a class you haven't seen yet
- Checking what a service depends on
- Finding which methods exist and their signatures before calling one

```json
{ "filePath": "/src/Orders/OrderService.cs" }
```

### When to call `inspect_context`

Call it when a task spans multiple files and you need to understand how types relate to each other — who depends on what, which interfaces are implemented by which classes, what's unresolved.

**Use it before:**
- Implementing a feature that touches a chain of services/repositories
- Tracing a dependency from controller to repository
- Understanding which types in a set are missing from the call (check `unresolved`)

```json
{
  "filePaths": [
    "/src/Orders/OrderController.cs",
    "/src/Orders/OrderService.cs",
    "/src/Orders/IOrderService.cs"
  ]
}
```

`unresolved` lists types referenced but not found in the file set — use it as a signal to call `inspect_context` again with those missing files included.

### When NOT to call these tools

- **You need method logic** — use `read_file` with the `startLine`/`endLine` from `inspect_file` output
- **You need to edit code** — read the actual source, these tools have no line-level content
- **Non-C# files** — only `.cs` is supported
- **More than 15 files for `inspect_context`** — split into smaller batches

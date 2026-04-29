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

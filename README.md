<!-- mcp-name: io.github.nushey/context-manager -->

<p align="center">
  <img src="https://raw.githubusercontent.com/nushey/ContextManager/main/assets/logo.svg" width="128" alt="ContextManager logo">
</p>

<h1 align="center">ContextManager</h1>

<p align="center">
  <strong>Structural C# context for AI coding agents, powered by Roslyn.</strong><br>
  <em>Navigate before you read.</em>
</p>

<p align="center">
  <a href="https://www.nuget.org/packages/ContextManager"><img src="https://img.shields.io/nuget/v/ContextManager.svg" alt="NuGet version"></a>
</p>

<p align="center">
  <a href="#install">Install</a> &bull;
  <a href="#tools">Tools</a> &bull;
  <a href="#how-to-use-it">How to use it</a> &bull;
  <a href="#faq">FAQ</a> &bull;
  <a href="INSTALL.md">Full install guide</a>
</p>

---

ContextManager is an [MCP](https://modelcontextprotocol.io) server that saves an agent from reading
whole C# files. It does two things:

- **Inspects files.** It turns a `.cs` file into a JSON contract with its types, public members,
  constructor dependencies, base types, interfaces, and events. Method bodies, private members, and
  XML docs are left out; each method carries its line range so the agent can read only the body it
  needs.
- **Maps the solution.** It builds a knowledge graph of a `.sln` so the agent can find which files
  matter, and how risky a change is, before opening any of them.

## Install

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10) or later.

**macOS / Linux:**

```bash
curl -fsSL https://raw.githubusercontent.com/nushey/ContextManager/main/scripts/install.sh | bash
```

**Windows (PowerShell):**

```powershell
irm https://raw.githubusercontent.com/nushey/ContextManager/main/scripts/install.ps1 | iex
```

The installer installs or updates the `ContextManager` .NET global tool and runs
`context-manager setup`, a wizard that adds the server to the MCP clients you pick (Claude Code,
Claude Desktop, Codex, Antigravity, Kilo Code, Cursor, Devin, Opencode).

To install by hand instead:

```bash
dotnet tool install -g ContextManager
claude mcp add --scope user context-manager -- context-manager   # Claude Code
```

For other clients, flags, config-file paths, and troubleshooting, see [INSTALL.md](INSTALL.md).

> `project_scan` on `.NET Framework 4.8` solutions only works on Windows and needs
> [Visual Studio Build Tools](https://visualstudio.microsoft.com/downloads/) with the
> **.NET desktop build tools** workload. Everything else works on any platform.

## Tools

| Tool | What it returns |
|------|-----------------|
| `inspect_file` | The structural contract of one `.cs` file. |
| `inspect_context` | A compact view of up to 15 `.cs` files plus the references between them; `unresolved` lists the user types missing from the set. |
| `project_scan` | Builds or refreshes the solution's graph and reports its coverage. |
| `graph_search` | Files and methods matching possible names, grouped by file with namespaces and method node IDs. |
| `graph_get_dependencies` | The neighbors of a node, rolled up to their declaring type, with edge counts per kind. |
| `graph_impact_analysis` | Every type that depends on a node, directly or transitively, with its file. |
| `graph_path_find` | The shortest dependency path between two nodes. |

Graph tools take the path of the `.sln` and keep the graph up to date on their own: before
answering, they re-extract only what changed since the last scan. The graph is saved in
`.context-manager/<solution name>.graph.json` next to the `.sln`, so every session on that solution
shares it.

## How to use it

### Start from a file

When the user mentions a file, start there:

1. `inspect_file(path)`: read the type and build its node ID as `Namespace.TypeName`.
2. `graph_get_dependencies(solutionPath, nodeId)`: find the neighbors worth reading.
3. `inspect_file` on those neighbors only.
4. `graph_impact_analysis(solutionPath, nodeId)`: check the blast radius before changing anything.

A short impact list means the change is contained. A long one means the type's public contract is
load-bearing and should be preserved; it does not mean the agent has to read every file in it.

Node IDs use Roslyn's fully qualified format, for example `MyApp.Orders.OrderService` or
`MyApp.Orders.OrderService.GetOrderAsync(System.Guid)`. Pass the IDs that the graph tools return
as they are.

```json
// graph_impact_analysis("/abs/path/MyApp.sln", "MyApp.Orders.IOrderRepository")
{
  "affectedIds": ["MyApp.Orders.OrderService", "MyApp.Api.OrdersController"],
  "affectedFiles": {
    "MyApp.Orders.OrderService": "/abs/path/Orders/OrderService.cs",
    "MyApp.Api.OrdersController": "/abs/path/Api/OrdersController.cs"
  }
}
```

### Find code by name

When you know roughly what something is called but not which file it is in, and you have no node ID,
call `graph_search` with several candidate names:

```json
{"solutionPath":"/abs/path/MyApp.sln","queries":["Login","Authentication","SignIn"]}
```

**What it searches.** A term matches when it appears anywhere in a file name (without `.cs`) or a
method name (without its signature). Matching is ordinal and ignores case, and any one term is enough.
It searches every method already in the graph, private ones included, and finds constructors under
their type name. It does not search directory paths, parameter types, method bodies, or namespaces.

**Valid terms.** Terms are trimmed and must have at least 3 characters. An empty list, or a single
term that is too short, rejects the whole query before the graph is synchronized, with code
`invalid_query` and message
`Provide at least one query term; each term must be at least 3 characters long after trimming whitespace.`

**Order and limit.** Exact matches come first, then partial ones; within each group, results keep
the order in which they appear in the graph. A name that matches several terms does not rank higher.
The search keeps at most **50 matches**, and the limit counts matches, not files: each matching file
counts once and each matching method ID counts once, even when several terms match it. There is no
pagination. When `truncated` is `true`, at least one match was left out; use more specific names to
narrow the search.

```json
{"results":[{"file":"/abs/path/Auth/Session.cs","namespaces":["MyApp.Auth"],"fileMatched":false,"methods":[{"name":"Login","nodeId":"MyApp.Auth.Session.Login()"}]}],"truncated":false}
```

**Response.** The response is one line of compact JSON, grouped by file. Every file entry has `file`,
`namespaces`, `fileMatched`, and `methods`, and empty arrays stay as `[]`. `namespaces` lists every
namespace in the file. `fileMatched` is `true` when the file's own name is one of the matches kept
within the limit. A file can also appear only because one of its methods was kept, even if its name
matched too but fell outside the limit. With no matches, the response is
`{"results":[],"truncated":false}`.

From there, call `inspect_file` on a returned `file`, or pass a method's `nodeId` unchanged to the
other graph tools. A graph saved by an older version is re-extracted once to add names and
namespaces; later queries reuse the updated graph while nothing changes.

To make your agent follow these workflows, copy [`docs/AGENTS-template.md`](docs/AGENTS-template.md)
into the `AGENTS.md` of your project.

## FAQ

**Is it a background service?** No. It is an MCP stdio server: the client starts it when a session
opens and stops it when the session ends.

**What state does it keep?** Only the graph files written next to each `.sln`. The inspection tools
read the files you pass and keep nothing.

**Does it read my whole solution?** Only the graph tools do. `inspect_file` and `inspect_context`
read just the files you give them.

## Build and test

```bash
dotnet build
dotnet test
```

## License

MIT © nushey

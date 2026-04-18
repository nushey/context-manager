# Design: Context Manager MCP — Phase 1 (inspect_file MVP)

## Existing conventions honored
- Source of truth: `AGENTS.md` (pre-existing at repo root).
- Conventions bootstrap: pre-existing.
- Language & framework: C# 12 on .NET 8 LTS. `Nullable` and `ImplicitUsings` enabled (inherited from current `ContextManager.csproj`).
- Folder structure pattern: `src/` for production projects and `tests/` for tests — the plan's layout (`context-manager-csharp-final.md` §"Estructura de la solución") is adopted now because Phase 2 will add a second server-side tool and the split keeps analysis reusable without later reshuffling. AGENTS.md §"Repo layout" explicitly defers to `design.md` for this decision.
- Naming conventions (AGENTS.md §"Code Style"): file-scoped namespaces; one public type per file; filename matches the type; `record` for immutable data, `class` for behavior; `CancellationToken` last in async signatures.
- State / data-flow pattern: stateless, single-shot parse → extract → serialize. No caching (AGENTS.md §"Additional Notes": "stay stateless").
- Testing setup: MSTest, fixture `.cs` files under `tests/<Project>.Tests/Fixtures/`, one fixture per scenario (AGENTS.md §"Testing").
- Specific rules being honored:
  - "Roslyn visitors: prefer `CSharpSyntaxWalker` subclasses over hand-rolled recursion" (AGENTS.md §"Code Style"). Extractors inherit from `CSharpSyntaxWalker`.
  - "The MVP uses syntax trees only. A `CSharpCompilation` + `SemanticModel` is reserved for the future `inspect_context` tool" (AGENTS.md §"Additional Notes"). Matches scope AC: "File parsing uses the Roslyn syntax tree only".
  - "Output JSON must be deterministic for identical input on the same machine. Preserve declaration order from the syntax tree; do not sort alphabetically" (AGENTS.md §"Additional Notes").
  - "No packaging, publishing, or `dotnet tool install` steps in scope for the MVP" (AGENTS.md §"Build and Output"). No `PackAsTool`.
  - Conventional Commits + branch `feature/context-manager-mcp` already active (AGENTS.md §"Pull Request Guidelines").

## Technical approach
Reshape the repo into the `src/ContextManager.Mcp/` (console host) + `src/ContextManager.Analysis/` (class library) split proposed in the plan, plus a single test project `tests/ContextManager.Analysis.Tests/`. The MCP host wires `ModelContextProtocol` over stdio via `Microsoft.Extensions.Hosting` and registers `inspect_file` through the SDK's `[McpServerTool]` attribute discovery. The analysis library parses a single `.cs` file with `CSharpSyntaxTree.ParseText`, walks the tree with a small set of `CSharpSyntaxWalker` subclasses to fill immutable `record` output models, and the tool serializes them with `System.Text.Json` using deterministic, indented, camelCase settings. DTO classification happens at the end of type extraction using the three-branch heuristic from scope AC. No `CSharpCompilation`, no `SemanticModel`, no caching, no project-root walking.

## Files to create / modify
Create:
- `src/ContextManager.Mcp/ContextManager.Mcp.csproj` — console app, references `ContextManager.Analysis`. (create)
- `src/ContextManager.Mcp/Program.cs` — host builder, stdio transport, `WithToolsFromAssembly()`. (create)
- `src/ContextManager.Mcp/Tools/InspectFileTool.cs` — `[McpServerToolType]` class with a single `[McpServerTool(Name = "inspect_file")]` method. (create)
- `src/ContextManager.Mcp/Serialization/AnalysisJson.cs` — shared `JsonSerializerOptions` factory. (create)
- `src/ContextManager.Analysis/ContextManager.Analysis.csproj` — class library, `Microsoft.CodeAnalysis.CSharp` reference. (create)
- `src/ContextManager.Analysis/FileAnalyzer.cs` — entry point `Analyze(string filePath, CancellationToken ct)` returning `FileAnalysis`. (create)
- `src/ContextManager.Analysis/Extraction/TypeExtractor.cs` — `CSharpSyntaxWalker` that collects top-level and nested type declarations flat. (create)
- `src/ContextManager.Analysis/Extraction/MemberExtractor.cs` — static helpers that turn a `TypeDeclarationSyntax` (plus `RecordDeclarationSyntax`, `EnumDeclarationSyntax`) into `TypeInfo`. (create)
- `src/ContextManager.Analysis/Extraction/AttributeExtractor.cs` — renders attribute name + argument list to string. (create)
- `src/ContextManager.Analysis/Extraction/AccessLevel.cs` — static helper `FromModifiers(SyntaxTokenList)` → `string` ("public"|"internal"|"protected"|"private"). (create)
- `src/ContextManager.Analysis/Extraction/DtoDetector.cs` — three-branch heuristic, returns `bool IsDto(TypeDeclarationSyntax, string name)`. (create)
- `src/ContextManager.Analysis/Models/FileAnalysis.cs` — top-level output record. (create)
- `src/ContextManager.Analysis/Models/TypeInfo.cs` — type record. (create)
- `src/ContextManager.Analysis/Models/MethodInfo.cs` — method record. (create)
- `src/ContextManager.Analysis/Models/ParameterInfo.cs` — parameter record (shared by methods and `constructorDependencies`). (create)
- `src/ContextManager.Analysis/Models/PropertyInfo.cs` — property record. (create)
- `src/ContextManager.Analysis/Models/AnalysisError.cs` — structured error record returned when input is invalid. (create)
- `tests/ContextManager.Analysis.Tests/ContextManager.Analysis.Tests.csproj` — MSTest project. (create)
- `tests/ContextManager.Analysis.Tests/FileAnalyzerTests.cs` — tests per fixture. (create)
- `tests/ContextManager.Analysis.Tests/Fixtures/ServiceWithDependencies.cs` — class with DI constructor + method with attributes. (create)
- `tests/ContextManager.Analysis.Tests/Fixtures/OrderServiceInterface.cs` — plain interface. (create)
- `tests/ContextManager.Analysis.Tests/Fixtures/CreateOrderRecord.cs` — record with primary constructor. (create)
- `tests/ContextManager.Analysis.Tests/Fixtures/Money.cs` — struct. (create)
- `tests/ContextManager.Analysis.Tests/Fixtures/OrderStatus.cs` — enum. (create)
- `tests/ContextManager.Analysis.Tests/Fixtures/DtoByNoMethods.cs` — DTO branch (a). (create)
- `tests/ContextManager.Analysis.Tests/Fixtures/DtoByAutoProperties.cs` — DTO branch (b). (create)
- `tests/ContextManager.Analysis.Tests/Fixtures/CreateOrderRequest.cs` — DTO branch (c), suffix-based. (create)

Modify:
- `ContextManager.sln` — replace the single `ContextManager` project entry with the three new projects under `src/` and `tests/`. (modify)

Delete:
- `ContextManager/ContextManager.csproj` and `ContextManager/Program.cs` — the root console stub is superseded by `src/ContextManager.Mcp/`. (delete)

## Patterns / abstractions
- Reuse: `CSharpSyntaxWalker` (Roslyn) as the visitor abstraction (AGENTS.md mandates it). `System.Text.Json` source generator is NOT introduced — plain reflection serialization is enough at MVP scale; picking it avoids a second moving part.
- One `CSharpSyntaxWalker` subclass (`TypeExtractor`) collects `BaseTypeDeclarationSyntax` and `DelegateDeclarationSyntax` nodes into a flat list, so nested types surface at the top level without a second pass. Per-type detail is handled by plain static helpers in `MemberExtractor` — no walker needed below type level because the members are direct children.
- No new abstractions beyond the extractors/models listed above. No factory pattern, no DI container inside `ContextManager.Analysis`, no strategy pattern for DTO detection — a single method with three `if`s matches the heuristic in scope AC literally.

## Data models
All in `ContextManager.Analysis/Models/`. All `record`s for immutability and value equality. Properties in declaration order; that order is also the JSON key order.

```csharp
public sealed record FileAnalysis(
    string File,
    string? Namespace,
    IReadOnlyList<string> Usings,
    IReadOnlyList<TypeInfo> Types);

public sealed record TypeInfo(
    string Name,
    string Kind,                              // "class" | "interface" | "record" | "struct" | "enum" | "dto" | "delegate"
    string Access,                            // "public" | "internal" | "protected"
    string? Base,
    IReadOnlyList<string> Implements,
    IReadOnlyList<string> Attributes,
    IReadOnlyList<ParameterInfo> ConstructorDependencies,
    IReadOnlyList<MethodInfo> Methods,
    IReadOnlyList<PropertyInfo> Properties,
    IReadOnlyList<string>? Members);          // enum member names; null for non-enum

public sealed record MethodInfo(
    string Name,
    string Access,
    string ReturnType,
    IReadOnlyList<ParameterInfo> Parameters,
    IReadOnlyList<string> Attributes);

public sealed record ParameterInfo(string Type, string Name);

public sealed record PropertyInfo(string Name, string Type, string Access);

public sealed record AnalysisError(string Code, string Message, string? FilePath);
// Code ∈ { "file_not_found", "not_a_cs_file", "read_failed", "parse_failed" }
```

Notes:
- `Properties` is always emitted as an empty array for DTOs (scope AC: "omitted (or empty) and `kind` equals `\"dto\"`"). Empty array is chosen because it keeps the type's shape stable for consumers.
- `Members` is `null` for non-enum types and serialized away by the ignore-null setting; enums carry names only, not numeric values (scope AC).
- `ConstructorDependencies` is empty for types without a primary/explicit constructor. For records it is populated from the primary-constructor parameter list. Constructor parameters are never duplicated into `Methods` (scope AC).
- Delegates surface as a `TypeInfo` with `Kind = "delegate"`, populated `Base = null`, empty `Implements`/`Attributes`, and a single synthetic `MethodInfo` describing the invoke signature (name = delegate name, return type, parameters). This is the cheapest way to keep the flat `types` array uniform.
- Events surface as entries in the owning type's `Properties` list with `Access = event's access` and `Type = "event <EventType>"` — one-line contract, no new model. This is an intentional simplification; revisit in Phase 3 if agents need richer event metadata.

## Extraction algorithm
1. `FileAnalyzer.Analyze(path, ct)`:
   - Validate: path exists, extension is `.cs`. On failure return `AnalysisError` (caller serializes it). Never throw.
   - `var text = File.ReadAllText(path)`. Catch IO exceptions → `AnalysisError("read_failed")`.
   - `var tree = CSharpSyntaxTree.ParseText(text, cancellationToken: ct)`.
   - If `tree.GetDiagnostics().Any(d => d.Severity == Error)` → return `AnalysisError("parse_failed")` with the first error message. Partial trees still analyze; the MVP only short-circuits on fatal parse errors.
   - Get `var root = (CompilationUnitSyntax)tree.GetRoot(ct)`.
   - Extract `usings`: `root.Usings.Select(u => u.Name.ToString())`, preserved in source order.
   - Extract `namespace`: look for `FileScopedNamespaceDeclarationSyntax` first, then `NamespaceDeclarationSyntax`; use `.Name.ToString()`; `null` if neither exists.
   - Run `TypeExtractor` walker over `root`. The walker overrides `VisitClassDeclaration`, `VisitInterfaceDeclaration`, `VisitRecordDeclaration`, `VisitStructDeclaration`, `VisitEnumDeclaration`, `VisitDelegateDeclaration`. For each visited node it calls `MemberExtractor.Build(node)` to produce a `TypeInfo` and appends it to the flat list, then continues `base.Visit...` so nested types are also captured.
2. `MemberExtractor.Build`:
   - `name` = `.Identifier.ValueText`.
   - `access` = `AccessLevel.FromModifiers(node.Modifiers)`. Default-to-internal for top-level types; default-to-private for nested members. **Filter:** types with access `private` are excluded; members with access `private` are excluded (scope AC).
   - `base` and `implements` come from `BaseList?.Types`: first entry is treated as base class if it is a class-declaration's base list, otherwise interfaces. For interfaces and structs, all entries are interfaces. Implemented-vs-extended distinction for classes is made heuristically from the first entry's syntax — good enough without semantic model, matching the plan's §"Resolución" scope (deferred to Phase 2).
   - `attributes` via `AttributeExtractor.Render(attrList)` — joins `AttributeList.Attributes`, each as `Name[(args)]` where `args` is the verbatim `ArgumentList.ToString()` trimmed of parentheses; escaping for JSON is delegated to `System.Text.Json`.
   - For classes/records/structs/interfaces:
     - `constructorDependencies`: for records, use `RecordDeclarationSyntax.ParameterList`. For classes/structs, pick the single non-static `ConstructorDeclarationSyntax` from `Members` if exactly one exists; if multiple, pick the one with the most parameters (matches "DI constructor" convention); if none, empty. Each `ParameterSyntax` → `ParameterInfo(type.ToString(), identifier.ValueText)`.
     - `methods`: filter `Members.OfType<MethodDeclarationSyntax>()` by access level, map to `MethodInfo`. `returnType` = `method.ReturnType.ToString()`. Parameters same as above. Attributes via `AttributeExtractor`. Constructors NOT included in `methods`.
     - `properties`: filter `Members.OfType<PropertyDeclarationSyntax>()` by access. Also include `public const` and `public static readonly` `FieldDeclarationSyntax` entries (scope AC) as `PropertyInfo(name, type, access)`.
     - `DtoDetector.IsDto`: if true, override `kind` to `"dto"` and clear `properties` to empty. DTO check runs AFTER member extraction because branch (a) needs the method count.
   - For enums: `kind = "enum"`, `Members = enumNode.Members.Select(m => m.Identifier.ValueText)`, `Methods`/`Properties`/`ConstructorDependencies` empty.
   - For delegates: `kind = "delegate"`, emit synthetic method as described.
3. `DtoDetector.IsDto(node, name)` returns true if ANY:
   - (a) Zero non-compiler-generated `MethodDeclarationSyntax` children. (No semantic model → "compiler-generated" is treated as "none declared in source".)
   - (b) No `ConstructorDeclarationSyntax` with parameters AND every `PropertyDeclarationSyntax` child is an auto-property (each accessor has `Body == null` and `ExpressionBody == null`).
   - (c) `name` ends with any of: `Dto`, `Request`, `Response`, `Command`, `Query`, `Event`, `Model`, `ViewModel`.
4. `AccessLevel.FromModifiers`: returns the first of `public`, `protected`, `internal`, `private` present. `protected internal` collapses to `"protected"` (most restrictive visible access; good enough for contract surface). Absent modifiers: default to `"internal"` at type scope, `"private"` at member scope.

## JSON serialization
- Library: `System.Text.Json` (ships with .NET 8, no extra dependency).
- Shared options (`AnalysisJson.Options`):
  - `PropertyNamingPolicy = JsonNamingPolicy.CamelCase` (matches plan's output examples: `constructorDependencies`, `returnType`, `filePath`).
  - `WriteIndented = true` (matches plan examples and makes diffs reviewable).
  - `DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull` (so `Base`, `Namespace`, and enum `Members` vanish on non-applicable types).
  - `Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping` — keeps attribute argument text (e.g. `"api/[controller]"`) legible without escaping `[`/`]`.
  - Key order: record declaration order = JSON property order (System.Text.Json preserves declared order). Do NOT enable sorting.
  - Enum values: none of our enums are serialized; `Kind` is `string`.
- Output is UTF-8 without BOM.
- The tool returns the JSON string directly; the MCP SDK wraps it as tool content.

## MCP wiring
- `ContextManager.Mcp.csproj` packages:
  ```xml
  <PackageReference Include="ModelContextProtocol" Version="0.3.0-preview.4" />
  <PackageReference Include="Microsoft.Extensions.Hosting" Version="8.0.1" />
  ```
  `ModelContextProtocol` 0.3.0-preview.4 is the current stable preview on NuGet (verified via context7 lookup for `modelcontextprotocol/csharp-sdk`); the plan's "1.*" is aspirational, so we pin the latest available preview and flag this under Gaps. `Microsoft.Extensions.Hosting` uses the 8.x LTS band per the plan.
- `ContextManager.Analysis.csproj` packages:
  ```xml
  <PackageReference Include="Microsoft.CodeAnalysis.CSharp" Version="4.11.0" />
  ```
  (Latest stable 4.x at time of design.)
- `tests/ContextManager.Analysis.Tests.csproj` packages:
  ```xml
  <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
  <PackageReference Include="MSTest.TestFramework" Version="3.6.4" />
  <PackageReference Include="MSTest.TestAdapter" Version="3.6.4" />
  ```
- `Program.cs` shape (no method body — design only):
  ```csharp
  var builder = Host.CreateApplicationBuilder(args);
  builder.Logging.ClearProviders();               // stdio transport: nothing to stdout but protocol frames
  builder.Services
      .AddMcpServer()
      .WithStdioServerTransport()
      .WithToolsFromAssembly();                   // picks up [McpServerToolType] classes
  await builder.Build().RunAsync();
  ```
- `InspectFileTool.cs` shape:
  ```csharp
  [McpServerToolType]
  public sealed class InspectFileTool
  {
      [McpServerTool(Name = "inspect_file"),
       Description("Returns a structural JSON contract for a single C# file.")]
      public string Analyze(
          [Description("Absolute or working-directory-relative path to a .cs file.")] string filePath,
          CancellationToken cancellationToken)
          => /* call FileAnalyzer, serialize FileAnalysis or AnalysisError */;
  }
  ```
- Input schema: derived automatically by the SDK from the method signature — a single required string property `filePath`.
- Errors: file not found / wrong extension / read failure / parse failure return an `AnalysisError` serialized with the same options so the client receives a well-formed JSON body. The tool method does not throw; the MCP host stays up.

## Testing strategy
- Single MSTest project `tests/ContextManager.Analysis.Tests/` exercises `FileAnalyzer` end-to-end. The MCP host is NOT tested in Phase 1 — its `Program.cs` is one-liner glue over the SDK; adding an MCP integration test harness is deferred (scope does not require it; AGENTS.md allows test introduction alongside the first test project).
- **Assertion style: targeted property assertions, not golden JSON files.** Rationale: golden files drift when any irrelevant field changes and force mass regeneration; targeted assertions make it obvious which contract clause is being verified, and scope AC is enumerated at clause granularity. One exception — a single `FileAnalyzer_IsDeterministic` test invokes the analyzer twice on the same fixture and `Assert.Equal`s the serialized JSON byte-for-byte; this protects the determinism AC without committing to a full golden corpus.
- Fixtures are real `.cs` files under `Fixtures/` with `<None Include="Fixtures\**\*.cs" CopyToOutputDirectory="PreserveNewest" />` in the test csproj so they exist next to the test binary at runtime. Filename is `.cs` but the `<Compile Remove="Fixtures\**" />` directive prevents them from being compiled as test code.
- Test coverage mirrors scope AC's final bullet: one fixture per scenario — service class with DI, interface, record with primary constructor, struct, enum, and three DTO branches (a/b/c).

## Open decisions resolved
1. **Semantic model usage** — RESOLVED: `inspect_file` uses the Roslyn syntax tree only. No `CSharpCompilation`, no `SemanticModel` constructed. Cites plan §"Preguntas abiertas" #3 ("`inspect_file` usa solo syntax tree") and AGENTS.md §"Additional Notes". Matches scope AC explicitly.
2. Plan's open question #1 (accept type names vs paths in `inspect_context`) — DEFERRED to Phase 2 (out of scope here).
3. Plan's open question #2 (auto-merge partial classes) — DEFERRED to Phase 3. For Phase 1, a `partial` class is analyzed as a standalone declaration with only the members present in the given file; no `"partial": true` flag is emitted. Scope AC also defers this.

## Trade-offs
- Chose the two-project split (`Mcp` + `Analysis`) over a single-project MVP because (a) the plan already anticipates it, (b) Phase 2 adds `inspect_context` which will reuse `FileAnalyzer`, and (c) it physically separates the MCP transport noise from the pure analyzer, making xUnit tests trivial (no MCP host in the test project). The cost is one extra `.csproj` at MVP time — acceptable.
- Chose `record` for all output models over mutable `class`es because AGENTS.md §"Code Style" prescribes `record` for immutable data and System.Text.Json serializes positional records cleanly.
- Chose targeted assertions over golden JSON files (see "Testing strategy" rationale).
- Chose plain `System.Text.Json` over a source generator. Source generator is faster but adds a second moving part for trivial payloads; defer to Phase 2 only if profiling shows a hotspot.
- Chose to classify "access" with a single string rather than an enum (even though C# favors enums) because the JSON output is the canonical contract and strings match the plan's output examples directly. No conversion layer needed.
- Chose to represent events as single-line `PropertyInfo` entries and delegates as synthetic methods rather than adding two new model types — scope AC only requires "name and signature" for both; more structure is YAGNI.

## Out of scope (technical)
- `inspect_context`, multi-file analysis, `CSharpCompilation`, `SemanticModel`, cross-reference map, and the `unresolved` list (Phase 2).
- Partial-class auto-merging and a `"partial": true` flag (Phase 3).
- Generic-constraint rendering, nullable reference type annotations on return/parameter types, `required`-property handling as first-class features (Phase 3).
- `PackAsTool`, `<ToolCommandName>`, NuGet publication, dotnet-tool installation instructions (Phase 3; AGENTS.md §"Build and Output" forbids MVP packaging).
- Caching, file watchers, incremental analysis (intentionally never added per AGENTS.md and scope).
- Project-root walking (searching upward for a `.csproj`) — not required because no semantic model is built.
- Compiler-generated member filtering beyond "not declared in source" — without a semantic model we cannot enumerate true compiler-generated members; the MVP treats source-declared as the ground truth.
- MCP host integration tests spawning the stdio server — not required by scope AC; revisit in Phase 2.
- Any refactor of currently non-existent code (the repo is a `Hello, World!` stub; nothing to preserve besides the solution file structure).

## Gaps for human attention
- `ModelContextProtocol` on NuGet is still shipped as `0.x-preview.*`, not `1.*` as the plan asserts. The design pins `0.3.0-preview.4`. If the PO or Developer prefers to hold for `1.0.0-*`, the tool-registration API surface (`WithToolsFromAssembly`, `[McpServerTool]`) is stable across late 0.x previews, so migration to 1.x should be a version bump rather than a code change — but this is a plan inaccuracy worth acknowledging.
- AGENTS.md's §"Repo layout" describes the current single-project tree. This design deletes that project in favor of the `src/` + `tests/` layout. AGENTS.md itself states "do not preemptively reorganize" and defers to `design.md`, so this is sanctioned — but the Verifier should update AGENTS.md's layout section once this feature lands so the doc stops describing a tree that no longer exists.

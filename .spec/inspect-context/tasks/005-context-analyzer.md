# 005 — Add ContextAnalyzer with MSTest coverage

## Files
- `src/ContextManager.Analysis/ContextAnalyzer.cs` — create; orchestrates multi-file parse, compilation, and cross-reference resolution
- `tests/ContextManager.Analysis.Tests/ContextAnalyzerTests.cs` — create; fixture-backed MSTest class

## Description
Create `public class ContextAnalyzer` in namespace `ContextManager.Analysis`. It is the orchestration entry point for `inspect_context`, analogous to `FileAnalyzer` for `inspect_file`. It receives `CrossReferenceResolver` via constructor injection.

**Primary method:**

```csharp
public async Task<ContextAnalysis> AnalyzeAsync(
    IReadOnlyList<string> filePaths,
    CancellationToken ct = default)
```

**Pipeline per `design.md §Technical approach`:**

1. Read each file's source text with `File.ReadAllTextAsync`.
2. Parse each source text with `CSharpSyntaxTree.ParseText(source, path: filePath)` to get a `SyntaxTree` with a populated `FilePath`.
3. Build a `CSharpCompilation.Create(...)` from all syntax trees — no metadata references (source-text only, per `design.md §Key decision resolutions #1`).
4. For each file: obtain `compilation.GetSemanticModel(tree)`, then run `TypeExtractor` to walk the AST and collect `TypeInfo` objects.
5. Convert each `TypeInfo` to a `ContextTypeInfo`: map `Name`, `Kind`, `Base`, `Implements`, `Attributes`, `ConstructorDependencies`; format `Methods` using `MethodSignatureFormatter.Format` per method; omit `Properties` entirely.
6. Build the `IReadOnlyList<ContextFileAnalysis>` preserving declaration order.
7. Call `CrossReferenceResolver.Resolve(files, compilation, treeByPath, ct)` to get `(References, Unresolved)`.
8. Return `new ContextAnalysis(Files: files, References: references, Unresolved: unresolved)`.

Input validation (15-file cap, missing-file checks) is NOT done here — that is `InspectContextTool`'s responsibility. `ContextAnalyzer` can assume valid input.

**MSTest coverage in `ContextAnalyzerTests.cs`** against the four fixture files from task 003:

| Test | What it verifies |
|------|-----------------|
| `AnalyzeAsync_WithAllFourFixtures_ReturnsThreeFiles` | `files` has 3 entries when called with the 3 files that exist (IOrderService, IOrderRepository, OrderService) |
| `AnalyzeAsync_OrderServiceImplementsIOrderService_InReferences` | `references` contains `{ From="OrderService", To="IOrderService", Via="implements", ResolvedFile=<path> }` |
| `AnalyzeAsync_OrderServiceDependsOnIOrderRepository_InReferences` | `references` contains `{ From="OrderService", To="IOrderRepository", Via="constructor", ResolvedFile=<path> }` |
| `AnalyzeAsync_OrderController_CreateOrderDtoIsUnresolved` | `unresolved` contains `"CreateOrderDto"` when OrderController.cs is in the set but the DTO file is not |
| `AnalyzeAsync_MethodsAreStrings_NotObjects` | `types[x].Methods` contains strings like `"GetOrderAsync(int, CancellationToken): Task<Order>"` |
| `AnalyzeAsync_PropertiesNeverInOutput` | no `ContextTypeInfo` in any file result has... (verify `Methods` is the only collection, no properties field exists at type level) |
| `AnalyzeAsync_DeterministicOutput_SameInputSameJson` | serializing `ContextAnalysis` twice with `AnalysisJson.Options` yields identical strings |

Fixture paths should be resolved using `Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Fixtures", "ContextFixtures", "<filename>.cs")` — matching the pattern used by `FileAnalyzerTests`.

## Acceptance
- [ ] `ContextAnalyzer` exists, is a `public class`, accepts `CrossReferenceResolver` via constructor
- [ ] `AnalyzeAsync` returns `Task<ContextAnalysis>` and accepts `CancellationToken` as last param
- [ ] All seven MSTest methods listed above exist and pass
- [ ] `ContextTypeInfo.Methods` are strings (formatted by `MethodSignatureFormatter`)
- [ ] `ContextTypeInfo` has no properties field
- [ ] `dotnet test` passes (all existing + new tests green)

## Needs tests
yes
Tool = MSTest
Location = `tests/ContextManager.Analysis.Tests/ContextAnalyzerTests.cs`

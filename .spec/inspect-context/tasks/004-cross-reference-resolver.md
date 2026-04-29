# 004 — Add CrossReferenceResolver

## Files
- `src/ContextManager.Analysis/Extraction/CrossReferenceResolver.cs` — create; resolves structural references via SemanticModel

## Description
Create `public class CrossReferenceResolver` in namespace `ContextManager.Analysis.Extraction`. This class is the only place in the codebase where Roslyn `SemanticModel` is used. It is registered in DI (`AddSingleton`) and injected into `ContextAnalyzer` (next task).

**Primary method signature:**

```csharp
public (IReadOnlyList<ReferenceInfo> References, IReadOnlyList<string> Unresolved)
    Resolve(
        IReadOnlyList<ContextFileAnalysis> files,
        CSharpCompilation compilation,
        IReadOnlyDictionary<string, SyntaxTree> treeByPath,
        CancellationToken ct = default)
```

**Logic per `design.md §Key decision resolutions #2 and #4`:**

For each `ContextFileAnalysis` in `files`, obtain `compilation.GetSemanticModel(treeByPath[file.File])`. For each `ContextTypeInfo` in `file.Types`:
- **`base`**: if `type.Base` is non-null, attempt to resolve via `model.GetTypeInfo(...)` on the base type syntax node; emit `ReferenceInfo { From = type.Name, To = type.Base, Via = "base", ResolvedFile = ... }`.
- **`implements`**: for each entry in `type.Implements`, emit `ReferenceInfo { Via = "implements" }`.
- **`constructor`**: for each entry in `type.ConstructorDependencies`, emit `ReferenceInfo { Via = "constructor" }`.
- **`parameter`**: for each distinct parameter type across all non-private methods, emit `ReferenceInfo { Via = "parameter" }`.

`resolvedFile` is `symbol.DeclaringSyntaxReferences.FirstOrDefault()?.SyntaxTree.FilePath` — cross-checked against the set of input file paths.

`unresolved` = all type names appearing as `To` in any `ReferenceInfo` where `resolvedFile == null`, deduplicated, in first-occurrence order.

`references` are deduplicated by `(From, To, Via)` triple — first-occurrence order (declaration order from syntax tree walk).

**Important**: `CrossReferenceResolver` works from the already-extracted `ContextFileAnalysis` structural data. It does NOT re-walk the syntax tree from scratch — it uses the type/method/dependency strings already extracted by `TypeExtractor`. To resolve symbol names from strings, it must also receive the raw syntax nodes. Adjust the signature if needed to receive `SyntaxTree` objects alongside the compilation so `GetSemanticModel` can be called per file.

Refer to `design.md §Key decision resolutions #1` for the null-resolution strategy: `GetSymbolInfo().Symbol == null` → treat as unresolved, not an error.

File-scoped namespace `ContextManager.Analysis.Extraction`. No behavior in models. `CancellationToken` is the last parameter.

## Acceptance
- [ ] `CrossReferenceResolver` exists and is a `public class` (not static — it is DI-registered)
- [ ] It accepts a `CSharpCompilation` and produces `(References, Unresolved)`
- [ ] `references` are deduplicated by `(From, To, Via)` triple
- [ ] `unresolved` contains type names with `resolvedFile == null`, deduplicated
- [ ] `dotnet build` passes

## Needs tests
no
(Tested as part of `ContextAnalyzerTests` in task 005, which drives the full pipeline against fixture files — isolated unit tests of the resolver require complex Roslyn setup not justified by scope.)

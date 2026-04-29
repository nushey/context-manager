# fix-001 — Remove dead semantic model assignment in ContextAnalyzer

## Files
- `src/ContextManager.Analysis/ContextAnalyzer.cs` — modify (delete one dead line)

## Description
Inside the per-file loop in `ContextAnalyzer.AnalyzeAsync`, line 43 assigns a local variable
that is never used:

```csharp
var model = compilation.GetSemanticModel(tree);
```

The namespace detection that follows (lines 47–50) operates purely on the syntax tree root.
The actual semantic model work is performed by `CrossReferenceResolver.Resolve()`, which calls
`GetSemanticModel` independently for each file. This local assignment is dead code that fires
a wasted Roslyn API call on every file in the loop.

**Exact change**: delete line 43 (`var model = compilation.GetSemanticModel(tree);`) and nothing
else. No other logic changes; the loop body, the extractor, and the resolver call are all
unchanged.

## Acceptance
- [ ] `src/ContextManager.Analysis/ContextAnalyzer.cs` no longer contains the line
      `var model = compilation.GetSemanticModel(tree);`
- [ ] No other lines in the file are modified
- [ ] `dotnet build` exits with code 0 (no warnings about unused variables remain)
- [ ] `dotnet test` exits with code 0 (all existing tests still green)

## Needs tests
no
(The fix is a pure deletion of dead code; the MSTest suite in
`tests/ContextManager.Analysis.Tests/ContextAnalyzerTests.cs` already covers the correct
behavior of `ContextAnalyzer.AnalyzeAsync` and will confirm nothing regressed.)

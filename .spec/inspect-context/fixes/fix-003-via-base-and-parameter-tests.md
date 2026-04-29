# fix-003 — Add fixture and tests for via="base" and via="parameter" branches

## Context files (read for understanding — do not modify)
- `src/ContextManager.Analysis/Extraction/CrossReferenceResolver.cs` — lines 43–61 produce `via = "base"` (when `contextType.Base` matches the base-list entry) and lines 101–124 produce `via = "parameter"` (method parameter types on non-private methods). Both branches exist in the code but are not exercised by any current test.
- `tests/ContextManager.Analysis.Tests/ContextAnalyzerTests.cs` — existing test class; new test methods are added here alongside the existing ones. Review fixture helper `FixturePath()` and `CreateAnalyzer()` before writing new tests.
- `tests/ContextManager.Analysis.Tests/Fixtures/ContextFixtures/OrderService.cs` — shows the existing fixture shape: file-scoped namespace `ContextFixtures`, `using` directives at the top, minimal bodies with `throw new System.NotImplementedException()`.

## Reference files (STRICT STYLE MATCH)
- `tests/ContextManager.Analysis.Tests/ContextAnalyzerTests.cs` — Gold Standard for fixture-backed `ContextAnalyzer` tests: `FixturePath()` helper, `CreateAnalyzer()`, `Assert.IsNotNull` + `Assert.AreEqual` pattern, descriptive failure messages in assertion calls.

## Required Skills
none

## Files to create/modify (suggested)
- `tests/ContextManager.Analysis.Tests/Fixtures/ContextFixtures/BaseOrderService.cs` — create (abstract base class that `OrderService` can extend, giving the resolver a concrete `via = "base"` target within the file set)
- `tests/ContextManager.Analysis.Tests/Fixtures/ContextFixtures/OrderService.cs` — modify (add `BaseOrderService` to the inheritance list so the existing fixture produces a `via = "base"` reference when `BaseOrderService.cs` is in the input set)
- `tests/ContextManager.Analysis.Tests/ContextAnalyzerTests.cs` — modify (add two new `[TestMethod]` methods; do not change existing methods)

## Description
Two branches in `CrossReferenceResolver` are untested:

**`via = "base"`** — fired when a type declaration's base-list entry matches `contextType.Base` (i.e., the entry is a class, not an interface). Currently every fixture's inheritance is interface-only (`IOrderService`, `IOrderRepository`), so this branch never executes in tests.

Create `BaseOrderService.cs` as an abstract class in `ContextFixtures` namespace. Modify `OrderService.cs` to extend `BaseOrderService` in addition to implementing `IOrderService` (the `Base` field that `TypeExtractor` extracts will be `"BaseOrderService"`; the `Implements` list will contain `"IOrderService"`). Add a test method that passes `[BaseOrderServicePath, OrderServicePath, IOrderServicePath, IOrderRepositoryPath]` and asserts that a reference with `From = "OrderService"`, `To = "BaseOrderService"`, `Via = "base"` is present and `ResolvedFile` is not null.

**`via = "parameter"`** — fired for each parameter type on non-private methods. The existing fixtures already have methods with parameters (e.g., `GetOrderAsync(int id, CancellationToken ct)` in `IOrderService`), but no test asserts on `via = "parameter"` entries. Add a test method that passes the three-file set and asserts that a reference with `Via = "parameter"` exists (e.g., `From = "IOrderService"`, `To = "CancellationToken"`, `Via = "parameter"`). Because `CancellationToken` is a BCL type (no source in the set), `ResolvedFile` must be null and `"CancellationToken"` must appear in `result.Unresolved` — this simultaneously covers the `via = "parameter"` branch and confirms the documented BCL-unresolved behavior.

Both new test methods belong in `ContextAnalyzerTests.cs`. Do not split them into a separate file.

## Acceptance
- [ ] `tests/ContextManager.Analysis.Tests/Fixtures/ContextFixtures/BaseOrderService.cs` exists as an abstract class in namespace `ContextFixtures`
- [ ] `tests/ContextManager.Analysis.Tests/Fixtures/ContextFixtures/OrderService.cs` inherits from `BaseOrderService` in addition to implementing `IOrderService` (i.e., `: BaseOrderService, IOrderService`)
- [ ] `ContextAnalyzerTests` contains a test method asserting `Via == "base"` with `ResolvedFile != null` for `OrderService → BaseOrderService`
- [ ] `ContextAnalyzerTests` contains a test method asserting a reference with `Via == "parameter"` exists and that its `To` type appears in `result.Unresolved` when it is a BCL type
- [ ] All previously passing tests in `ContextAnalyzerTests` remain green (modifying `OrderService.cs` must not break `AnalyzeAsync_OrderServiceImplementsIOrderService_InReferences` or `AnalyzeAsync_OrderServiceDependsOnIOrderRepository_InReferences`)
- [ ] `dotnet test` exits with code 0

## Needs tests
yes
(tool = MSTest, location = `tests/ContextManager.Analysis.Tests/ContextAnalyzerTests.cs`)

---

## Implementation log (filled by dev after successful commit)
- Commit: 2479fed — test(inspect-context): add via=base and via=parameter fixture and tests
- Files modified:
  - tests/ContextManager.Analysis.Tests/Fixtures/ContextFixtures/BaseOrderService.cs (created)
  - tests/ContextManager.Analysis.Tests/Fixtures/ContextFixtures/OrderService.cs (modified)
  - tests/ContextManager.Analysis.Tests/ContextAnalyzerTests.cs (modified)
- Tests added: 2 (MSTest)
- Context & Reference files read:
  - src/ContextManager.Analysis/Extraction/CrossReferenceResolver.cs
  - tests/ContextManager.Analysis.Tests/ContextAnalyzerTests.cs
  - tests/ContextManager.Analysis.Tests/Fixtures/ContextFixtures/OrderService.cs
- Notes: Also read MemberExtractor.cs and ContextAnalyzer.cs to confirm how Base vs Implements is determined (I-prefix heuristic in MemberExtractor.ExtractBaseList) and to verify that compilation errors do not fail the analysis (ContextAnalyzer builds source-text compilation without diagnostics check). Added `override` keyword to OrderService.GetOrderAsync to keep the fixture syntactically correct when extending the abstract base. All 115 tests green.

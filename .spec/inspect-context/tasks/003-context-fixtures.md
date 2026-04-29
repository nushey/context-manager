# 003 — Add fixture .cs files for ContextAnalyzer tests

## Files
- `tests/ContextManager.Analysis.Tests/Fixtures/ContextFixtures/IOrderService.cs` — create; interface fixture
- `tests/ContextManager.Analysis.Tests/Fixtures/ContextFixtures/IOrderRepository.cs` — create; interface fixture
- `tests/ContextManager.Analysis.Tests/Fixtures/ContextFixtures/OrderService.cs` — create; implements `IOrderService`, constructor depends on `IOrderRepository`
- `tests/ContextManager.Analysis.Tests/Fixtures/ContextFixtures/OrderController.cs` — create; depends on `IOrderService`, method parameter references `CreateOrderDto` (not in the set)

## Description
Create four real `.cs` fixture files that will be parsed at test runtime by `ContextAnalyzerTests`. These are not test files — they are C# source files representing production-like shapes. They must be valid C# 12 / .NET 8 syntax and must be included in the test project as `Content` with `CopyToOutputDirectory = Always` (check the existing `.csproj` for the pattern already applied to other fixtures).

**`IOrderService.cs`** — namespace `ContextFixtures`; `public interface IOrderService` with at minimum one method (e.g., `Task<Order> GetOrderAsync(int id, CancellationToken ct)`).

**`IOrderRepository.cs`** — namespace `ContextFixtures`; `public interface IOrderRepository` with at minimum one method (e.g., `Task<Order> FindAsync(int id, CancellationToken ct)`).

**`OrderService.cs`** — namespace `ContextFixtures`; `public class OrderService : IOrderService`; constructor `public OrderService(IOrderRepository repository)`; implements the method from `IOrderService`.

**`OrderController.cs`** — namespace `ContextFixtures`; `public class OrderController`; constructor `public OrderController(IOrderService service)`; one public method that takes a `CreateOrderDto` parameter (e.g., `public Task CreateAsync(CreateOrderDto dto, CancellationToken ct)`). `CreateOrderDto` is intentionally NOT declared in any fixture file — this produces the `unresolved` entry.

All fixtures use file-scoped namespaces. Keep method bodies minimal (e.g., `throw new NotImplementedException();` or `=> throw new NotImplementedException();`).

## Acceptance
- [ ] All four `.cs` files exist under `tests/ContextManager.Analysis.Tests/Fixtures/ContextFixtures/`
- [ ] Each file contains exactly the type described and compiles as valid C#
- [ ] `dotnet build` on the test project succeeds (fixture files are valid C#, even if the types they reference don't all exist in the fixture set)
- [ ] The fixture `.csproj` entry (if needed) copies files to the output directory

## Needs tests
no

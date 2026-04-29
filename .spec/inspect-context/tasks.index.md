# Tasks: inspect_context — Multi-File Cross-Reference Tool

Project has tests: yes
Test tool: MSTest

| ID  | Title                                    | Files touched                                                                                                                                                                                                                                 |
|-----|------------------------------------------|-----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| 001 | Add output model records                 | `src/ContextManager.Analysis/Models/ContextTypeInfo.cs`, `ReferenceInfo.cs`, `ContextFileAnalysis.cs`, `ContextAnalysis.cs`                                                                                                                  |
| 002 | Add MethodSignatureFormatter             | `src/ContextManager.Analysis/Extraction/MethodSignatureFormatter.cs`, `tests/ContextManager.Analysis.Tests/Extraction/MethodSignatureFormatterTests.cs`                                                                                      |
| 003 | Add ContextFixtures .cs files            | `tests/ContextManager.Analysis.Tests/Fixtures/ContextFixtures/IOrderService.cs`, `IOrderRepository.cs`, `OrderService.cs`, `OrderController.cs`                                                                                              |
| 004 | Add CrossReferenceResolver               | `src/ContextManager.Analysis/Extraction/CrossReferenceResolver.cs`                                                                                                                                                                           |
| 005 | Add ContextAnalyzer + MSTest coverage   | `src/ContextManager.Analysis/ContextAnalyzer.cs`, `tests/ContextManager.Analysis.Tests/ContextAnalyzerTests.cs`                                                                                                                              |
| 006 | Add InspectContextTool MCP tool          | `src/ContextManager.Mcp/Tools/InspectContextTool.cs`                                                                                                                                                                                         |
| 007 | Wire DI registrations in Program.cs      | `src/ContextManager.Mcp/Program.cs`                                                                                                                                                                                                           |

## Fixes

| Fix ID  | Title                              | Triggered by failure in | Files touched                                        |
|---------|------------------------------------|-------------------------|------------------------------------------------------|
| fix-001 | Remove dead semantic model assignment | Verifier cycle 1     | `src/ContextManager.Analysis/ContextAnalyzer.cs`    |
| fix-002 | Add MSTest coverage for InspectContextTool validation paths | Quality audit | `tests/ContextManager.Analysis.Tests/Tools/InspectContextToolTests.cs` |
| fix-003 | Add fixture and tests for via="base" and via="parameter" branches | Quality audit | `tests/ContextManager.Analysis.Tests/Fixtures/ContextFixtures/BaseOrderService.cs`, `OrderService.cs`, `tests/ContextManager.Analysis.Tests/ContextAnalyzerTests.cs` |
| fix-004 | Regenerate AGENTS.md to remove stale inspect_context reference | Quality audit | `AGENTS.md` |
| fix-005 | Document BCL-types-in-unresolved behavior in tool description | Quality audit | `src/ContextManager.Mcp/Tools/InspectContextTool.cs`, `src/ContextManager.Analysis/Models/ContextAnalysis.cs` |

# fix-002 — Add MSTest coverage for InspectContextTool validation paths

## Context files (read for understanding — do not modify)
- `src/ContextManager.Mcp/Tools/InspectContextTool.cs` — the two validation branches to cover: `filePaths.Count > 15` (returns `too_many_files`) and `!File.Exists(path)` (returns `file_not_found`)
- `src/ContextManager.Mcp/Serialization/AnalysisJson.cs` — serialization options used by the tool; needed to deserialize the returned JSON in assertions
- `tests/ContextManager.Analysis.Tests/FileAnalyzerTests.cs` — reference for how error-path tests are structured against the existing MCP-adjacent analysis layer

## Reference files (STRICT STYLE MATCH)
- `tests/ContextManager.Analysis.Tests/FileAnalyzerTests.cs` — Gold Standard for error-path test structure: direct instantiation, assertion on `Code` field, no mocking

## Required Skills
none

## Files to create/modify (suggested)
- `tests/ContextManager.Analysis.Tests/Tools/InspectContextToolTests.cs` — create (new test class covering the two validation branches)

## Description
`InspectContextTool.InspectContextAsync` contains two validation branches (lines 25–36 of the current implementation) that have zero test coverage:

1. `filePaths.Count > 15` — returns a JSON-serialized `AnalysisError` with code `"too_many_files"`.
2. `!File.Exists(path)` — returns a JSON-serialized `AnalysisError` with code `"file_not_found"` and the offending path in `FilePath`.

Create a new test class `InspectContextToolTests` in `tests/ContextManager.Analysis.Tests/Tools/`. The class instantiates `InspectContextTool` directly (injecting a real `ContextAnalyzer` + `CrossReferenceResolver`, same pattern as `ContextAnalyzerTests.CreateAnalyzer()`). Each test calls `InspectContextAsync`, deserializes the returned JSON string with `AnalysisJson.Options`, and asserts on the `Code` and `FilePath` fields of `AnalysisError`.

Two test methods are required:
- `InspectContextAsync_TooManyFiles_ReturnsTooManyFilesError` — pass a list of 16 dummy path strings (existence irrelevant because the count check fires first); assert `Code == "too_many_files"`.
- `InspectContextAsync_MissingFile_ReturnsFileNotFoundError` — pass a list with one nonexistent path; assert `Code == "file_not_found"` and `FilePath == <the nonexistent path>`.

## Acceptance
- [ ] `tests/ContextManager.Analysis.Tests/Tools/InspectContextToolTests.cs` exists with both test methods
- [ ] Both test methods are decorated with `[TestMethod]` and the class with `[TestClass]`
- [ ] `InspectContextAsync_TooManyFiles_ReturnsTooManyFilesError` passes a list of exactly 16 paths and asserts `Code == "too_many_files"`
- [ ] `InspectContextAsync_MissingFile_ReturnsFileNotFoundError` passes a list with one nonexistent path and asserts `Code == "file_not_found"` and `FilePath` equals the nonexistent path
- [ ] `dotnet test` exits with code 0 (all tests green, including the new ones)

## Needs tests
yes
(tool = MSTest, location = `tests/ContextManager.Analysis.Tests/Tools/InspectContextToolTests.cs`)

---

## Implementation log (filled by dev after successful commit)
- Commit: 5a5c350 — test(inspect-context): add MSTest coverage for InspectContextTool validation paths
- Files modified:
  - tests/ContextManager.Analysis.Tests/Tools/InspectContextToolTests.cs (created)
- Tests added: 2 (MSTest)
- Context & Reference files read:
  - src/ContextManager.Mcp/Tools/InspectContextTool.cs
  - src/ContextManager.Mcp/Serialization/AnalysisJson.cs
  - tests/ContextManager.Analysis.Tests/FileAnalyzerTests.cs
- Notes: Created the Tools/ subdirectory under tests/ContextManager.Analysis.Tests/ (did not previously exist). Also read tests/ContextManager.Analysis.Tests/ContextAnalyzerTests.cs to confirm the CreateAnalyzer() pattern (new ContextAnalyzer(new CrossReferenceResolver())) — this file is not in the task's context list but was needed to determine correct direct instantiation for InspectContextTool.

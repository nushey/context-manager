# 002 — Add MethodSignatureFormatter static helper

## Files
- `src/ContextManager.Analysis/Extraction/MethodSignatureFormatter.cs` — create; static helper formatting a `MethodInfo` as a one-line string

## Description
Create a `public static class MethodSignatureFormatter` in namespace `ContextManager.Analysis.Extraction`. It exposes a single public method:

```
public static string Format(MethodInfo method)
```

The output format is `"MethodName(ParamType1, ParamType2): ReturnType"`. When the method has no parameters, output `"MethodName(): ReturnType"`. Parameter **names** are omitted — only the type is emitted per `design.md §Key decision resolutions #3`. This is a pure string transformation; it has no Roslyn dependency and does not modify `MethodInfo` or `MemberExtractor`.

Use `string.Join(", ", method.Parameters.Select(p => p.Type))` for the parameter list. `MethodInfo.Parameters` is an `IReadOnlyList<ParameterInfo>` where `ParameterInfo.Type` is a string.

File-scoped namespace; no XML doc comments (internal API).

## Acceptance
- [ ] `MethodSignatureFormatter.Format` exists and is `public static`
- [ ] Format with parameters: `"DoThing(string, int): bool"`
- [ ] Format with no parameters: `"GetAll(): IReadOnlyList<Order>"`
- [ ] `dotnet build` produces zero errors

## Needs tests
yes
Tool = MSTest
Location = `tests/ContextManager.Analysis.Tests/Extraction/MethodSignatureFormatterTests.cs`

Tests must cover: method with multiple parameters, method with no parameters, method whose return type contains generics (e.g., `IReadOnlyList<Order>`). No fixture file needed — construct `MethodInfo` directly in the test (it is a plain record with no Roslyn coupling).

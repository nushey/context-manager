namespace ContextManager.Analysis.Models;

public sealed record ContextTypeInfo(
    string Name,
    string Kind,
    string? Base,
    IReadOnlyList<string>? Implements,
    IReadOnlyList<string>? Attributes,
    IReadOnlyList<string>? ConstructorDependencies,
    IReadOnlyList<string>? Methods);

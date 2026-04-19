namespace ContextManager.Analysis.Models;

public sealed record TypeInfo(
    string Name,
    string Kind,
    string Access,
    string? Base,
    IReadOnlyList<string>? Implements,
    IReadOnlyList<string>? Attributes,
    IReadOnlyList<ParameterInfo>? ConstructorDependencies,
    IReadOnlyList<MethodInfo>? Methods,
    IReadOnlyList<PropertyInfo>? Properties,
    IReadOnlyList<string>? Members,
    bool? IsPartial = null);

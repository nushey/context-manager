namespace ContextManager.Analysis.Models;

public sealed record MethodInfo(
    string Name,
    string Access,
    string ReturnType,
    IReadOnlyList<ParameterInfo> Parameters,
    IReadOnlyList<string> Attributes);

namespace ContextManager.Analysis.Models;

public sealed record MethodInfo(
    string Name,
    string Access,
    string ReturnType,
    int StartLine,
    int EndLine,
    IReadOnlyList<ParameterInfo>? Parameters,
    IReadOnlyList<string>? Attributes);

using ContextManager.Analysis.Models;

namespace ContextManager.Analysis.Extraction;

public static class MethodSignatureFormatter
{
    public static string Format(MethodInfo method)
    {
        var paramList = method.Parameters is { Count: > 0 }
            ? string.Join(", ", method.Parameters.Select(p => p.Type))
            : string.Empty;

        return $"{method.Name}({paramList}): {method.ReturnType}";
    }
}

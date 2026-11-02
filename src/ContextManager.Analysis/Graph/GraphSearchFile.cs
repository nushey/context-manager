namespace ContextManager.Analysis.Graph;

public sealed record GraphSearchFile(string File, IReadOnlyList<string> Namespaces, bool FileMatched, IReadOnlyList<GraphSearchMethod> Methods);

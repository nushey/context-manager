namespace ContextManager.Analysis.Graph;

public sealed record GraphNode(string Id, string Kind, string? File = null, string? Name = null, string? Namespace = null)
{
    public bool Equals(GraphNode? other) => other is not null && Id == other.Id;

    public override int GetHashCode() => Id.GetHashCode(StringComparison.Ordinal);
}

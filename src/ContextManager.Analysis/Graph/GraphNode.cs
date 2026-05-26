namespace ContextManager.Analysis.Graph;

// Node kinds: Class, Interface, Record, Method, Property
// Equality is by Id only — QuikGraph uses vertex equality for all graph operations.
public sealed record GraphNode(string Id, string Kind)
{
    public bool Equals(GraphNode? other) => other is not null && Id == other.Id;

    public override int GetHashCode() => Id.GetHashCode(StringComparison.Ordinal);
}

using ContextManager.Analysis.Models;

namespace ContextManager.Analysis.Graph;

public sealed class GraphEnricher : IGraphEnricher
{
    private const int MaxEndpoints = 5;

    private readonly GraphRegistry _registry;

    public GraphEnricher(GraphRegistry registry)
    {
        _registry = registry;
    }

    public FileAnalysis Enrich(FileAnalysis analysis, string filePath)
    {
        var (graph, status) = Resolve(filePath);
        if (graph is null)
            return status is null ? analysis : analysis with { GraphStatus = status };

        var fullPath = Path.GetFullPath(filePath);
        var types = analysis.Types
            .Select(type => FindTypeNode(graph, analysis.Namespace, type.Name, fullPath) is { } node
                ? type with { Graph = Describe(graph, node) }
                : type)
            .ToList();

        return analysis with { Types = types };
    }

    public ContextAnalysis Enrich(ContextAnalysis analysis)
    {
        if (analysis.Unresolved.Count == 0)
            return analysis;

        var graphs = analysis.Files
            .Select(f => Resolve(f.File).Graph)
            .OfType<GraphStore>()
            .ToList();

        var hints = new List<UnresolvedGraphHint>();
        var seen = new HashSet<UnresolvedGraphHint>();
        foreach (var type in analysis.Unresolved)
        {
            foreach (var node in graphs.SelectMany(g => g.TryGetShortNameMatches(type)))
            {
                var hint = new UnresolvedGraphHint(type, node.Id, node.File);
                if (seen.Add(hint))
                    hints.Add(hint);
            }
        }

        return hints.Count == 0 ? analysis : analysis with { UnresolvedHints = hints };
    }

    private (GraphStore? Graph, string? Status) Resolve(string filePath)
    {
        var location = _registry.TryResolveSolutionPath(filePath);
        if (location is null)
            return (null, null);

        if (File.Exists(location.GraphPath))
        {
            try
            {
                return (_registry.TryLoadGraph(location.GraphPath)?.Capture(), null);
            }
            catch (Exception)
            {
                return (null, null);
            }
        }

        return location.SolutionPaths.Count == 1
            ? (null, $"not_initialized (run project_scan on {location.SolutionPaths[0]})")
            : (null, null);
    }

    private static GraphNode? FindTypeNode(GraphStore graph, string? ns, string typeName, string filePath)
    {
        var qualifiedName = ns is null ? typeName : $"{ns}.{typeName}";
        if (graph.TryGetNode(qualifiedName, out var node) && NodeClassifier.IsTypeKind(node!.Kind))
            return node;

        var declaredHere = graph.TryGetShortNameMatches(typeName)
            .Where(n => string.Equals(n.File, filePath, StringComparison.Ordinal))
            .ToList();

        return declaredHere.Count == 1 ? declaredHere[0] : null;
    }

    private static TypeGraphContext Describe(GraphStore graph, GraphNode node)
    {
        var neighbors = graph.GetAggregatedNeighbors(node.Id);
        var inbound = neighbors.Where(n => n.Direction == "in").ToList();
        var outbound = neighbors.Where(n => n.Direction == "out").ToList();

        return new TypeGraphContext(
            node.Id,
            inbound.Count,
            outbound.Count,
            Endpoints(inbound),
            Endpoints(outbound));
    }

    private static IReadOnlyList<GraphEndpoint>? Endpoints(IReadOnlyList<GraphNeighbor> neighbors) =>
        neighbors.Count == 0
            ? null
            : neighbors.Take(MaxEndpoints).Select(n => new GraphEndpoint(n.Id, n.File)).ToList();
}

using ContextManager.Analysis.Extraction;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;

namespace ContextManager.Analysis.Graph;

public class GraphBuilder
{
    private readonly GraphStore _store;
    private readonly EdgeExtractor _edgeExtractor;

    public GraphBuilder(GraphStore store, EdgeExtractor edgeExtractor)
    {
        _store = store;
        _edgeExtractor = edgeExtractor;
    }

    public async Task<GraphStore> BuildAsync(string solutionPath, CancellationToken ct = default)
    {
        // Rebuild into a staging snapshot; readers keep observing the previous graph until
        // CommitRebuild publishes the new one. On failure, AbortRebuild preserves the previous
        // graph and releases the writer lock so the next scan does not deadlock.
        _store.BeginRebuild();
        try
        {
            using var workspace = MSBuildWorkspace.Create();
            var solution = await workspace.OpenSolutionAsync(solutionPath, cancellationToken: ct);

            foreach (var project in solution.Projects)
            {
                var compilation = await project.GetCompilationAsync(ct) as CSharpCompilation;
                if (compilation is null)
                    continue;

                foreach (var document in project.Documents)
                {
                    if (!IsSourceDocument(document.FilePath))
                        continue;

                    var syntaxTree = await document.GetSyntaxTreeAsync(ct);
                    if (syntaxTree is null)
                        continue;

                    var root = await syntaxTree.GetRootAsync(ct) as CompilationUnitSyntax;
                    if (root is null)
                        continue;

                    var model = compilation.GetSemanticModel(syntaxTree);

                    HarvestNodes(model, root, ct);

                    var edges = _edgeExtractor.Extract(model, root, ct);
                    foreach (var edge in edges)
                        _store.AddEdge(edge);
                }
            }

            _store.CommitRebuild();
        }
        catch
        {
            _store.AbortRebuild();
            throw;
        }

        return _store;
    }

    private void HarvestNodes(SemanticModel model, CompilationUnitSyntax root, CancellationToken ct)
    {
        var typeDeclarations = root.DescendantNodes().OfType<TypeDeclarationSyntax>();

        foreach (var typeDecl in typeDeclarations)
        {
            var typeSymbol = model.GetDeclaredSymbol(typeDecl, ct) as INamedTypeSymbol;
            if (typeSymbol is null)
                continue;

            var typeNode = TypeNodeFor(typeSymbol);
            if (typeNode is null)
                continue;

            _store.AddNode(typeNode);

            foreach (var memberDecl in typeDecl.Members)
            {
                switch (memberDecl)
                {
                    case MethodDeclarationSyntax methodDecl:
                    {
                        var methodSymbol = model.GetDeclaredSymbol(methodDecl, ct) as IMethodSymbol;
                        if (methodSymbol is null) continue;
                        var methodNode = NodeClassifier.NodeFor(methodSymbol);
                        if (methodNode is not null)
                            _store.AddNode(methodNode);
                        break;
                    }
                    case PropertyDeclarationSyntax propertyDecl:
                    {
                        var propSymbol = model.GetDeclaredSymbol(propertyDecl, ct) as IPropertySymbol;
                        if (propSymbol is null) continue;
                        var propNode = NodeClassifier.NodeFor(propSymbol);
                        if (propNode is not null)
                            _store.AddNode(propNode);
                        break;
                    }
                }
            }
        }
    }

    private static GraphNode? TypeNodeFor(INamedTypeSymbol symbol) => NodeClassifier.NodeFor(symbol);

    private static bool IsSourceDocument(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return false;

        if (filePath.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase))
            return false;

        if (filePath.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            return false;

        if (filePath.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }
}

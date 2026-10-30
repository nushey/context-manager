using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ContextManager.Analysis.Graph;

public class GraphBuilder
{
    private readonly EdgeExtractor _edgeExtractor;

    public GraphBuilder(EdgeExtractor edgeExtractor)
    {
        _edgeExtractor = edgeExtractor;
    }

    /// <summary>
    /// Extracts the contribution of each selected document of an evaluated C# project (every
    /// document when <paramref name="selectedDocuments"/> is null). A document maps to null when it
    /// is skipped: generated/obj/bin sources, or a tree that cannot be read. When the project has no
    /// compilation, nothing is extracted and <see cref="GraphProjectExtraction.Loaded"/> is false.
    /// </summary>
    public async Task<GraphProjectExtraction> ExtractProjectAsync(
        Project project,
        string projectKey,
        int projectOrder,
        IReadOnlySet<DocumentId>? selectedDocuments,
        CancellationToken ct = default)
    {
        var compilationTimer = Stopwatch.StartNew();
        var compilation = await project.GetCompilationAsync(ct) as CSharpCompilation;
        compilationTimer.Stop();

        var documents = new Dictionary<DocumentId, GraphDocumentRecord?>();
        if (compilation is null)
            return new GraphProjectExtraction(false, documents, compilationTimer.Elapsed, TimeSpan.Zero);

        var extractionTimer = Stopwatch.StartNew();
        for (var documentOrder = 0; documentOrder < project.DocumentIds.Count; documentOrder++)
        {
            ct.ThrowIfCancellationRequested();
            var documentId = project.DocumentIds[documentOrder];
            if (selectedDocuments is not null && !selectedDocuments.Contains(documentId))
                continue;

            documents[documentId] = await ExtractDocumentAsync(
                project.GetDocument(documentId)!, compilation, projectKey, projectOrder, documentOrder, ct);
        }

        extractionTimer.Stop();
        return new GraphProjectExtraction(true, documents, compilationTimer.Elapsed, extractionTimer.Elapsed);
    }

    private async Task<GraphDocumentRecord?> ExtractDocumentAsync(
        Document document,
        CSharpCompilation compilation,
        string projectKey,
        int projectOrder,
        int documentOrder,
        CancellationToken ct)
    {
        if (!IsSourceDocument(document.FilePath))
            return null;

        var syntaxTree = await document.GetSyntaxTreeAsync(ct);
        if (syntaxTree is null)
            return null;

        if (await syntaxTree.GetRootAsync(ct) is not CompilationUnitSyntax root)
            return null;

        var model = compilation.GetSemanticModel(syntaxTree);
        var text = await document.GetTextAsync(ct);

        return new GraphDocumentRecord(
            projectKey,
            Path.GetFullPath(document.FilePath!),
            Convert.ToHexString(text.GetChecksum().AsSpan()),
            projectOrder,
            documentOrder,
            HarvestNodes(model, root, document.FilePath, ct),
            _edgeExtractor.Extract(model, root, ct));
    }

    private static IReadOnlyList<GraphNode> HarvestNodes(SemanticModel model, CompilationUnitSyntax root, string? filePath, CancellationToken ct)
    {
        var nodes = new List<GraphNode>();
        var typeDeclarations = root.DescendantNodes().OfType<TypeDeclarationSyntax>();

        foreach (var typeDecl in typeDeclarations)
        {
            var typeSymbol = model.GetDeclaredSymbol(typeDecl, ct) as INamedTypeSymbol;
            if (typeSymbol is null)
                continue;

            var typeNode = NodeClassifier.NodeFor(typeSymbol, filePath);
            if (typeNode is null)
                continue;

            nodes.Add(typeNode);

            foreach (var memberDecl in typeDecl.Members)
            {
                ISymbol? memberSymbol = memberDecl switch
                {
                    MethodDeclarationSyntax methodDecl => model.GetDeclaredSymbol(methodDecl, ct),
                    PropertyDeclarationSyntax propertyDecl => model.GetDeclaredSymbol(propertyDecl, ct),
                    _ => null
                };

                var memberNode = memberSymbol is null ? null : NodeClassifier.NodeFor(memberSymbol, filePath);
                if (memberNode is not null)
                    nodes.Add(memberNode);
            }

            foreach (var (positionalProperty, _) in EdgeExtractor.PositionalProperties(model, typeDecl, typeSymbol, ct))
            {
                if (NodeClassifier.NodeFor(positionalProperty, filePath) is { } propertyNode)
                    nodes.Add(propertyNode);
            }

            foreach (var constructor in typeSymbol.InstanceConstructors)
            {
                if (NodeClassifier.NodeFor(constructor, filePath) is { } constructorNode)
                    nodes.Add(constructorNode);
            }
        }

        if (EdgeExtractor.TopLevelNodes(model, root, ct) is var (programNode, entryPointNode))
        {
            nodes.Add(programNode);
            nodes.Add(entryPointNode);
        }

        return nodes;
    }

    internal static bool IsSourceDocument(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return false;

        // Normalize to '/' so the obj/bin checks hold for both separators on every platform.
        // Path.DirectorySeparatorChar is unusable here: on Unix it equals the alt separator,
        // so a backslash path would slip through unnormalized.
        var p = filePath.Replace('\\', '/');

        if (p.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase))
            return false;

        if (p.Contains("/obj/", StringComparison.OrdinalIgnoreCase))
            return false;

        if (p.Contains("/bin/", StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }
}

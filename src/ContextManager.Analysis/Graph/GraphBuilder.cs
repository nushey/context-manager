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
        await BuildWithReportAsync(solutionPath, null, ct);
        return _store;
    }

    public async Task<GraphBuildResult> BuildWithReportAsync(
        string solutionPath,
        Func<string, CancellationToken, Task>? persistSnapshot,
        CancellationToken ct = default)
    {
        await _store.BeginRebuildAsync(ct);
        var rebuildActive = true;
        try
        {
            using var workspace = MSBuildWorkspace.Create();
            var diagnostics = new List<GraphBuildDiagnostic>();
            workspace.WorkspaceFailed += (_, args) =>
            {
                lock (diagnostics)
                    diagnostics.Add(new GraphBuildDiagnostic(args.Diagnostic.Kind.ToString(), args.Diagnostic.Message));
            };

            var solution = await workspace.OpenSolutionAsync(solutionPath, cancellationToken: ct);
            var totalProjects = solution.ProjectIds.Count;
            var loadedProjects = 0;
            var unsupportedProjects = 0;
            var skippedProjects = 0;
            var totalDocuments = 0;
            var loadedDocuments = 0;
            var skippedDocuments = 0;

            foreach (var project in solution.Projects)
            {
                ct.ThrowIfCancellationRequested();
                if (project.Language != LanguageNames.CSharp)
                {
                    unsupportedProjects++;
                    continue;
                }

                totalDocuments += project.DocumentIds.Count;
                var compilation = await project.GetCompilationAsync(ct) as CSharpCompilation;
                if (compilation is null)
                {
                    skippedProjects++;
                    skippedDocuments += project.DocumentIds.Count;
                    continue;
                }

                loadedProjects++;

                foreach (var document in project.Documents)
                {
                    if (!IsSourceDocument(document.FilePath))
                    {
                        skippedDocuments++;
                        continue;
                    }

                    var syntaxTree = await document.GetSyntaxTreeAsync(ct);
                    if (syntaxTree is null)
                    {
                        skippedDocuments++;
                        continue;
                    }

                    var root = await syntaxTree.GetRootAsync(ct) as CompilationUnitSyntax;
                    if (root is null)
                    {
                        skippedDocuments++;
                        continue;
                    }

                    var model = compilation.GetSemanticModel(syntaxTree);

                    HarvestNodes(model, root, ct);

                    var edges = _edgeExtractor.Extract(model, root, ct);
                    foreach (var edge in edges)
                        _store.AddEdge(edge);

                    loadedDocuments++;
                }
            }

            List<GraphBuildDiagnostic> diagnosticSnapshot;
            lock (diagnostics)
                diagnosticSnapshot = diagnostics.ToList();
            var hasFailures = diagnosticSnapshot.Any(d => d.Kind == WorkspaceDiagnosticKind.Failure.ToString());
            var status = loadedProjects == 0 || loadedDocuments == 0
                ? "failed"
                : _store.RebuildNodeCount == 0
                    ? "empty"
                    : hasFailures || unsupportedProjects > 0 || skippedDocuments > 0
                        ? "partial"
                        : "complete";

            var result = new GraphBuildResult(
                status,
                totalProjects,
                loadedProjects,
                unsupportedProjects,
                skippedProjects,
                totalDocuments,
                loadedDocuments,
                skippedDocuments,
                _store.RebuildNodeCount,
                _store.RebuildEdgeCount,
                diagnosticSnapshot);

            if (status is "failed" or "empty")
            {
                _store.AbortRebuild();
                rebuildActive = false;
                return result;
            }

            if (persistSnapshot is not null)
                await persistSnapshot(_store.SerializeRebuild(), ct);

            _store.CommitRebuild();
            rebuildActive = false;
            return result;
        }
        catch
        {
            if (rebuildActive)
                _store.AbortRebuild();
            throw;
        }
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

    internal static bool IsSourceDocument(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return false;

        // Normalize once so the obj/bin checks work regardless of whether the path uses the
        // platform separator or the alternate one (Roslyn may hand us forward-slash paths on Windows).
        var p = filePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

        if (p.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase))
            return false;

        var sep = Path.DirectorySeparatorChar;
        if (p.Contains($"{sep}obj{sep}", StringComparison.OrdinalIgnoreCase))
            return false;

        if (p.Contains($"{sep}bin{sep}", StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }
}

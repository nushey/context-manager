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
    /// is a generated, obj, or bin source. When the project has no
    /// compilation, nothing is extracted and <see cref="GraphProjectExtraction.Loaded"/> is false.
    /// </summary>
    public async Task<GraphProjectExtraction> ExtractProjectAsync(
        Project project,
        string projectKey,
        int projectOrder,
        IReadOnlySet<DocumentId>? selectedDocuments,
        CancellationToken ct = default)
    {
        var compilation = await project.GetCompilationAsync(ct) as CSharpCompilation;

        var documents = new Dictionary<DocumentId, GraphDocumentRecord?>();
        if (compilation is null)
            return new GraphProjectExtraction(false, documents);

        for (var documentOrder = 0; documentOrder < project.DocumentIds.Count; documentOrder++)
        {
            ct.ThrowIfCancellationRequested();
            var documentId = project.DocumentIds[documentOrder];
            if (selectedDocuments is not null && !selectedDocuments.Contains(documentId))
                continue;

            documents[documentId] = await ExtractDocumentAsync(
                project.GetDocument(documentId)!, compilation, projectKey, projectOrder, documentOrder, ct);
        }

        return new GraphProjectExtraction(true, documents);
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

        var syntaxTree = (await document.GetSyntaxTreeAsync(ct))!;
        var root = (CompilationUnitSyntax)await syntaxTree.GetRootAsync(ct);
        var text = await document.GetTextAsync(ct);
        var (nodes, edges) = _edgeExtractor.Extract(compilation.GetSemanticModel(syntaxTree), root, ct);

        return new GraphDocumentRecord(
            projectKey,
            Path.GetFullPath(document.FilePath!),
            Convert.ToHexString(text.GetChecksum().AsSpan()),
            projectOrder,
            documentOrder,
            nodes,
            edges);
    }

    internal static bool IsSourceDocument(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return false;

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

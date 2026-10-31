using ContextManager.Analysis.Extraction;
using ContextManager.Analysis.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ContextManager.Analysis;

public class FileAnalyzer
{
    public async Task<FileAnalysisResult> AnalyzeAsync(string filePath, CancellationToken ct)
    {
        if (!File.Exists(filePath))
            return new FileAnalysisResult(null, new AnalysisError("file_not_found", $"File not found: {filePath}", filePath));

        if (!filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            return new FileAnalysisResult(null, new AnalysisError("not_a_cs_file", $"Not a C# file: {filePath}", filePath));

        string text;
        try
        {
            text = await File.ReadAllTextAsync(filePath, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new FileAnalysisResult(null, new AnalysisError("read_failed", ex.Message, filePath));
        }

        var fullPath = Path.GetFullPath(filePath);
        var tree = CSharpSyntaxTree.ParseText(text, path: fullPath, cancellationToken: ct);
        var root = (CompilationUnitSyntax)tree.GetRoot(ct);
        var usings = root.Usings
            .Select(u => u.Name!.ToString())
            .ToList();

        var extractor = new TypeExtractor(FrameworkCompilation.Create([tree], ct).GetSemanticModel(tree));
        extractor.Visit(root);

        var parseErrors = tree.GetDiagnostics(ct)
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.GetMessage())
            .ToList();

        var analysis = new FileAnalysis(
            File: fullPath,
            Namespace: TypeExtractor.NamespaceOf(root),
            Usings: usings,
            Types: extractor.Types,
            ParseErrors: parseErrors.Count > 0 ? parseErrors : null);

        return new FileAnalysisResult(analysis, null);
    }
}

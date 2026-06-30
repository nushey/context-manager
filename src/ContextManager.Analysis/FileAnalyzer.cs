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

        var tree = CSharpSyntaxTree.ParseText(text, cancellationToken: ct);

        // Best-effort: extract from whatever tree Roslyn returns even when it has diagnostics,
        // surfacing the error messages via ParseErrors. Only the catastrophic case — the syntax
        // root itself being unobtainable — falls back to a parse_failed error.
        CompilationUnitSyntax root;
        try
        {
            root = (CompilationUnitSyntax)tree.GetRoot(ct);
        }
        catch (Exception)
        {
            return new FileAnalysisResult(null, new AnalysisError("parse_failed", "Unable to obtain syntax tree root.", filePath));
        }

        var usings = root.Usings
            .Select(u => u.Name!.ToString())
            .ToList();

        string? ns = null;
        if (root.Members.OfType<FileScopedNamespaceDeclarationSyntax>().FirstOrDefault() is { } fileScoped)
            ns = fileScoped.Name.ToString();
        else if (root.Members.OfType<NamespaceDeclarationSyntax>().FirstOrDefault() is { } blockNs)
            ns = blockNs.Name.ToString();

        var extractor = new TypeExtractor();
        extractor.Visit(root);

        var parseErrors = tree.GetDiagnostics(ct)
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.GetMessage())
            .ToList();

        var analysis = new FileAnalysis(
            File: Path.GetFileName(filePath),
            Namespace: ns,
            Usings: usings,
            Types: extractor.Types,
            ParseErrors: parseErrors.Count > 0 ? parseErrors : null);

        return new FileAnalysisResult(analysis, null);
    }
}

using ContextManager.Analysis.Extraction;
using ContextManager.Analysis.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ContextManager.Analysis;

public static class FileAnalyzer
{
    public static object Analyze(string filePath, CancellationToken ct)
    {
        if (!File.Exists(filePath))
            return new AnalysisError("file_not_found", $"File not found: {filePath}", filePath);

        if (!filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            return new AnalysisError("not_a_cs_file", $"Not a C# file: {filePath}", filePath);

        string text;
        try
        {
            text = File.ReadAllText(filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new AnalysisError("read_failed", ex.Message, filePath);
        }

        var tree = CSharpSyntaxTree.ParseText(text, cancellationToken: ct);

        var diagnostics = tree.GetDiagnostics(ct);
        var firstError = diagnostics.FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error);
        if (firstError is not null)
            return new AnalysisError("parse_failed", firstError.GetMessage(), filePath);

        var root = (CompilationUnitSyntax)tree.GetRoot(ct);

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

        return new FileAnalysis(
            File: Path.GetFileName(filePath),
            Namespace: ns,
            Usings: usings,
            Types: extractor.Types);
    }
}

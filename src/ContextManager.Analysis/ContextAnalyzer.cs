using ContextManager.Analysis.Extraction;
using ContextManager.Analysis.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace ContextManager.Analysis;

public class ContextAnalyzer
{
    // BCL metadata references built once per process from the runtime's trusted platform
    // assemblies. Without them the compilation resolves nothing — every predefined/BCL type
    // becomes an error symbol and pollutes the unresolved list.
    private static readonly Lazy<IReadOnlyList<MetadataReference>> BclReferences = new(() =>
        AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpa
            ? tpa.Split(Path.PathSeparator)
                 .Where(File.Exists)
                 .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
                 .ToList()
            : (IReadOnlyList<MetadataReference>)[]);

    private readonly CrossReferenceResolver _resolver;

    public ContextAnalyzer(CrossReferenceResolver resolver)
    {
        _resolver = resolver;
    }

    public async Task<ContextAnalysis> AnalyzeAsync(
        IReadOnlyList<string> filePaths,
        CancellationToken ct = default)
    {
        var sources = new Dictionary<string, string>(filePaths.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var path in filePaths)
        {
            sources[path] = await File.ReadAllTextAsync(path, ct);
        }

        var treeByPath = new Dictionary<string, Microsoft.CodeAnalysis.SyntaxTree>(
            filePaths.Count, StringComparer.OrdinalIgnoreCase);

        foreach (var (path, source) in sources)
        {
            treeByPath[path] = CSharpSyntaxTree.ParseText(source, path: path, cancellationToken: ct);
        }

        var compilation = CSharpCompilation.Create(
            assemblyName: "ContextAnalysis",
            syntaxTrees: treeByPath.Values,
            references: BclReferences.Value);

        var files = new List<ContextFileAnalysis>(filePaths.Count);

        foreach (var path in filePaths)
        {
            var tree = treeByPath[path];
            var root = (Microsoft.CodeAnalysis.CSharp.Syntax.CompilationUnitSyntax)tree.GetRoot(ct);

            string? ns = null;
            if (root.Members.OfType<Microsoft.CodeAnalysis.CSharp.Syntax.FileScopedNamespaceDeclarationSyntax>().FirstOrDefault() is { } fileScoped)
                ns = fileScoped.Name.ToString();
            else if (root.Members.OfType<Microsoft.CodeAnalysis.CSharp.Syntax.NamespaceDeclarationSyntax>().FirstOrDefault() is { } blockNs)
                ns = blockNs.Name.ToString();

            var extractor = new TypeExtractor();
            extractor.Visit(root);

            var contextTypes = extractor.Types
                .Select(t => new ContextTypeInfo(
                    Name: t.Name,
                    Kind: t.Kind,
                    Base: t.Base,
                    Implements: t.Implements is { Count: > 0 } impl ? impl : null,
                    Attributes: t.Attributes is { Count: > 0 } attrs ? attrs : null,
                    ConstructorDependencies: t.ConstructorDependencies is { Count: > 0 } deps
                        ? deps.Select(d => d.Type).ToList()
                        : null,
                    Methods: t.Methods is { Count: > 0 } methods
                        ? methods.Select(MethodSignatureFormatter.Format).ToList()
                        : null))
                .ToList();

            files.Add(new ContextFileAnalysis(
                File: path,
                Namespace: ns,
                Types: contextTypes));
        }

        var (references, unresolved) = _resolver.Resolve(files, compilation, treeByPath, ct);

        return new ContextAnalysis(
            Files: files,
            References: references,
            Unresolved: unresolved);
    }
}

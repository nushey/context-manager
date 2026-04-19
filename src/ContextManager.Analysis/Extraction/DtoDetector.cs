using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ContextManager.Analysis.Extraction;

public static class DtoDetector
{
    private static readonly string[] Suffixes =
    [
        "Dto", "Request", "Response", "Command", "Query", "Event", "Model", "ViewModel"
    ];

    public static bool IsDto(TypeDeclarationSyntax node, string name)
    {
        // Behavioral types (inherits or implements) are never DTOs
        if (node.BaseList is not null && node.BaseList.Types.Count > 0)
            return false;

        // Branch (a): no method declarations at all
        if (!node.Members.OfType<MethodDeclarationSyntax>().Any())
            return true;

        // Branch (b): no parameterized constructor AND every property is an auto-property
        bool hasParameterizedCtor = node.Members
            .OfType<ConstructorDeclarationSyntax>()
            .Any(c => c.ParameterList.Parameters.Count > 0);

        if (!hasParameterizedCtor)
        {
            bool allAutoProperties = node.Members
                .OfType<PropertyDeclarationSyntax>()
                .All(p => p.AccessorList is not null &&
                          p.AccessorList.Accessors.All(a => a.Body == null && a.ExpressionBody == null));

            if (allAutoProperties)
                return true;
        }

        // Branch (c): name ends with a known DTO suffix
        foreach (var suffix in Suffixes)
        {
            if (name.EndsWith(suffix, StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ContextManager.Analysis.Extraction;

/// <summary>
/// Single source of truth for a type's dependency-bearing constructor parameters:
/// the C# 12 primary constructor (<see cref="TypeDeclarationSyntax.ParameterList"/>,
/// valid for classes, records, and structs alike) wins; otherwise the non-static
/// declared constructor with the most parameters. Returns null when the type has
/// neither.
/// </summary>
public static class ConstructorParameterLocator
{
    public static SeparatedSyntaxList<ParameterSyntax>? Locate(TypeDeclarationSyntax node)
    {
        if (node.ParameterList is not null)
            return node.ParameterList.Parameters;

        var ctors = node.Members
            .OfType<ConstructorDeclarationSyntax>()
            .Where(c => !c.Modifiers.Any(m => m.ValueText == "static"))
            .ToList();

        if (ctors.Count == 0)
            return null;

        return ctors.MaxBy(c => c.ParameterList.Parameters.Count)!.ParameterList.Parameters;
    }
}

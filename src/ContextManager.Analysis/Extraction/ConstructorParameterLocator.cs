using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ContextManager.Analysis.Extraction;

/// <summary>
/// Single source of truth for a type's dependency-bearing constructor parameters:
/// the C# 12 primary constructor of a class or struct (<see cref="TypeDeclarationSyntax.ParameterList"/>)
/// wins; otherwise the non-static declared constructor with the most parameters. A record's
/// positional parameter list declares properties, not dependencies, and is never returned.
/// Returns null when the type has neither.
/// </summary>
public static class ConstructorParameterLocator
{
    public static SeparatedSyntaxList<ParameterSyntax>? Locate(TypeDeclarationSyntax node)
    {
        if (node.ParameterList is not null && node is not RecordDeclarationSyntax)
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

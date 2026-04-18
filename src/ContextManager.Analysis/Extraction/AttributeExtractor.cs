using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ContextManager.Analysis.Extraction;

public static class AttributeExtractor
{
    public static IReadOnlyList<string> Render(SyntaxList<AttributeListSyntax> lists)
    {
        var result = new List<string>();

        foreach (var list in lists)
        {
            foreach (var attr in list.Attributes)
            {
                result.Add(attr.Name.ToString() + attr.ArgumentList?.ToString());
            }
        }

        return result;
    }
}

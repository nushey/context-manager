using Microsoft.CodeAnalysis;

namespace ContextManager.Analysis.Extraction;

public static class AccessLevel
{
    public static string FromModifiers(SyntaxTokenList modifiers, bool isTopLevelType)
    {
        bool hasPublic = false;
        bool hasProtected = false;
        bool hasInternal = false;
        bool hasPrivate = false;

        foreach (var token in modifiers)
        {
            switch (token.ValueText)
            {
                case "public":    hasPublic    = true; break;
                case "protected": hasProtected = true; break;
                case "internal":  hasInternal  = true; break;
                case "private":   hasPrivate   = true; break;
            }
        }

        if (hasPublic)    return "public";
        if (hasProtected) return "protected";
        if (hasInternal)  return "internal";
        if (hasPrivate)   return "private";

        return isTopLevelType ? "internal" : "private";
    }
}

using ContextManager.Analysis.Extraction;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ContextManager.Analysis.Tests.Extraction;

[TestClass]
public class AccessLevelTests
{
    private static SyntaxTokenList GetModifiersFromFirstMember(string snippet)
    {
        var tree = CSharpSyntaxTree.ParseText(snippet);
        var root = tree.GetCompilationUnitRoot();
        var member = root.Members[0];

        return member switch
        {
            BaseTypeDeclarationSyntax t => t.Modifiers,
            DelegateDeclarationSyntax d => d.Modifiers,
            _ => throw new InvalidOperationException("Unexpected member type")
        };
    }

    private static SyntaxTokenList GetModifiersFromNestedMember(string snippet)
    {
        var tree = CSharpSyntaxTree.ParseText(snippet);
        var root = tree.GetCompilationUnitRoot();
        var outer = (TypeDeclarationSyntax)root.Members[0];
        var inner = (BaseTypeDeclarationSyntax)outer.Members[0];
        return inner.Modifiers;
    }

    [TestMethod]
    public void FromModifiers_ExplicitPublic_ReturnsPublic()
    {
        var modifiers = GetModifiersFromFirstMember("public class Foo {}");
        Assert.AreEqual("public", AccessLevel.FromModifiers(modifiers, isTopLevelType: true));
    }

    [TestMethod]
    public void FromModifiers_ExplicitInternal_ReturnsInternal()
    {
        var modifiers = GetModifiersFromFirstMember("internal class Foo {}");
        Assert.AreEqual("internal", AccessLevel.FromModifiers(modifiers, isTopLevelType: true));
    }

    [TestMethod]
    public void FromModifiers_ExplicitPrivate_ReturnsPrivate()
    {
        var modifiers = GetModifiersFromNestedMember("class Outer { private class Inner {} }");
        Assert.AreEqual("private", AccessLevel.FromModifiers(modifiers, isTopLevelType: false));
    }

    [TestMethod]
    public void FromModifiers_ExplicitProtected_ReturnsProtected()
    {
        var modifiers = GetModifiersFromNestedMember("class Outer { protected class Inner {} }");
        Assert.AreEqual("protected", AccessLevel.FromModifiers(modifiers, isTopLevelType: false));
    }

    [TestMethod]
    public void FromModifiers_ProtectedInternal_ReturnsProtected()
    {
        var modifiers = GetModifiersFromNestedMember("class Outer { protected internal class Inner {} }");
        Assert.AreEqual("protected", AccessLevel.FromModifiers(modifiers, isTopLevelType: false));
    }

    [DataTestMethod]
    [DataRow(true,  "internal")]
    [DataRow(false, "private")]
    public void FromModifiers_NoModifiers_DefaultsByContext(bool isTopLevelType, string expected)
    {
        // Parse a snippet where the type has no explicit access modifier.
        // For top-level: a class with no modifier. For nested: same inside an outer class.
        string snippet = isTopLevelType
            ? "class Foo {}"
            : "class Outer { class Inner {} }";

        SyntaxTokenList modifiers = isTopLevelType
            ? GetModifiersFromFirstMember(snippet)
            : GetModifiersFromNestedMember(snippet);

        Assert.AreEqual(expected, AccessLevel.FromModifiers(modifiers, isTopLevelType));
    }
}

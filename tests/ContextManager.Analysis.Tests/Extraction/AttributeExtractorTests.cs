using ContextManager.Analysis.Extraction;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ContextManager.Analysis.Tests.Extraction;

[TestClass]
public class AttributeExtractorTests
{
    private static SyntaxList<AttributeListSyntax> GetAttributeLists(string snippet)
    {
        var tree = CSharpSyntaxTree.ParseText(snippet);
        var root = tree.GetCompilationUnitRoot();
        var type = (TypeDeclarationSyntax)root.Members[0];
        return type.AttributeLists;
    }

    private static SyntaxList<AttributeListSyntax> GetMethodAttributeLists(string snippet)
    {
        var tree = CSharpSyntaxTree.ParseText(snippet);
        var root = tree.GetCompilationUnitRoot();
        var type = (TypeDeclarationSyntax)root.Members[0];
        var method = (MethodDeclarationSyntax)type.Members[0];
        return method.AttributeLists;
    }

    [TestMethod]
    public void Render_ParameterlessAttribute_ReturnsNameOnly()
    {
        var lists = GetAttributeLists("[Authorize] public class Foo {}");
        var result = AttributeExtractor.Render(lists);
        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("Authorize", result[0]);
    }

    [TestMethod]
    public void Render_PositionalArgAttribute_ReturnsNameWithBracketsPreserved()
    {
        var lists = GetMethodAttributeLists(
            "public class Foo { [Route(\"api/[controller]\")] public void Bar() {} }");
        var result = AttributeExtractor.Render(lists);
        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("Route(\"api/[controller]\")", result[0]);
    }

    [TestMethod]
    public void Render_NamedArgAttribute_ReturnsNameWithNamedArg()
    {
        var lists = GetMethodAttributeLists(
            "public class Foo { [Authorize(Policy = \"Admin\")] public void Bar() {} }");
        var result = AttributeExtractor.Render(lists);
        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("Authorize(Policy = \"Admin\")", result[0]);
    }

    [TestMethod]
    public void Render_MultipleAttributeLists_FlattensInSourceOrder()
    {
        var lists = GetMethodAttributeLists(
            "public class Foo { [HttpGet][Authorize(Policy = \"Admin\")] public void Bar() {} }");
        var result = AttributeExtractor.Render(lists);
        Assert.AreEqual(2, result.Count);
        Assert.AreEqual("HttpGet", result[0]);
        Assert.AreEqual("Authorize(Policy = \"Admin\")", result[1]);
    }
}

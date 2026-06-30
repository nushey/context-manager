using ContextManager.Analysis.Graph;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ContextManager.Analysis.Tests.Graph;

[TestClass]
public class NodeClassifierTests
{
    // Builds an INamedTypeSymbol from an inline source string. Covers class/struct/interface/
    // record declarations (BaseTypeDeclarationSyntax), enums (BaseTypeDeclarationSyntax), and
    // delegates (DelegateDeclarationSyntax) so every TypeKind path can be exercised. Mirrors the
    // bare-compilation approach of EdgeExtractorTests.ParseWithCompilation — no references needed
    // since classification only reads TypeKind/IsRecord.
    private static INamedTypeSymbol GetSymbol(string source, string typeName)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create("NodeClassifierTest", syntaxTrees: [tree]);
        var model = compilation.GetSemanticModel(tree);
        var root = (CompilationUnitSyntax)tree.GetRoot();

        var node = root.DescendantNodes().First(n => n switch
        {
            BaseTypeDeclarationSyntax b => b.Identifier.Text == typeName,
            DelegateDeclarationSyntax d => d.Identifier.Text == typeName,
            _ => false
        });

        return (INamedTypeSymbol)model.GetDeclaredSymbol(node)!;
    }

    [TestMethod]
    public void ClassifyTypeKind_RecordStruct_IsRecord()
    {
        var sym = GetSymbol("public record struct Point(int X, int Y);", "Point");
        Assert.AreEqual("Record", NodeClassifier.ClassifyTypeKind(sym));
    }

    [TestMethod]
    public void ClassifyTypeKind_PlainStruct_IsClass()
    {
        var sym = GetSymbol("public struct Plain { public int A; }", "Plain");
        Assert.AreEqual("Class", NodeClassifier.ClassifyTypeKind(sym));
    }

    [TestMethod]
    public void ClassifyTypeKind_RecordClass_IsRecord()
    {
        var sym = GetSymbol("public record Foo(int X);", "Foo");
        Assert.AreEqual("Record", NodeClassifier.ClassifyTypeKind(sym));
    }

    [TestMethod]
    public void ClassifyTypeKind_PlainClass_IsClass()
    {
        var sym = GetSymbol("public class C { }", "C");
        Assert.AreEqual("Class", NodeClassifier.ClassifyTypeKind(sym));
    }

    [TestMethod]
    public void ClassifyTypeKind_Interface_IsInterface()
    {
        var sym = GetSymbol("public interface IThing { void Do(); }", "IThing");
        Assert.AreEqual("Interface", NodeClassifier.ClassifyTypeKind(sym));
    }

    [TestMethod]
    public void ClassifyTypeKind_Enum_IsNull()
    {
        var sym = GetSymbol("public enum Color { Red, Green }", "Color");
        Assert.IsNull(NodeClassifier.ClassifyTypeKind(sym));
    }

    [TestMethod]
    public void ClassifyTypeKind_Delegate_IsNull()
    {
        var sym = GetSymbol("public delegate void Handler(int x);", "Handler");
        Assert.IsNull(NodeClassifier.ClassifyTypeKind(sym));
    }

    // Resolves an IMethodSymbol from an inline source string — same bare-compilation approach as
    // GetSymbol, for exercising the member branches of NodeFor.
    private static IMethodSymbol GetMethodSymbol(string source, string methodName)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create("NodeClassifierTest", syntaxTrees: [tree]);
        var model = compilation.GetSemanticModel(tree);
        var root = (CompilationUnitSyntax)tree.GetRoot();

        var node = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .First(m => m.Identifier.Text == methodName);

        return (IMethodSymbol)model.GetDeclaredSymbol(node)!;
    }

    [TestMethod]
    public void NodeFor_MethodSymbol_IsMethodNodeWithDisplayStringId()
    {
        var sym = GetMethodSymbol("public class C { public void Do(int x) { } }", "Do");
        var node = NodeClassifier.NodeFor(sym);

        Assert.IsNotNull(node);
        Assert.AreEqual("Method", node!.Kind);
        Assert.AreEqual(sym.ToDisplayString(), node.Id);
    }
}

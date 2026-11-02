using ContextManager.Analysis.Graph;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ContextManager.Analysis.Tests.Graph;

[TestClass]
public class NodeClassifierTests
{
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
    public void NodeMetadata_DoesNotChangeIdentityOrHash()
    {
        var symbol = GetMethodSymbol("namespace N; public class C { public T Convert<T>(T value) => value; }", "Convert");
        var node = NodeClassifier.NodeFor(symbol)!;
        Assert.AreEqual("Convert", node.Name);
        Assert.AreEqual("N", node.Namespace);
        Assert.AreEqual(symbol.ToDisplayString(), node.Id);
        var old = new GraphNode(node.Id, "Other");
        Assert.AreEqual(old, node);
        Assert.AreEqual(old.GetHashCode(), node.GetHashCode());
        Assert.IsNull(NodeClassifier.NodeFor(GetSymbol("public class Global { }", "Global"))!.Namespace);
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

    [TestMethod]
    public void NodeFor_SourceSymbol_UsesDeclaringTreePathNormalized()
    {
        var tree = CSharpSyntaxTree.ParseText("namespace N; public class C { }", path: "src/../src/C.cs");
        var compilation = CSharpCompilation.Create("NodeClassifierFileTest", syntaxTrees: [tree]);
        var symbol = compilation.GetTypeByMetadataName("N.C")!;

        var node = NodeClassifier.NodeFor(symbol, "/fallback/Other.cs");

        Assert.AreEqual(Path.GetFullPath("src/C.cs"), node?.File);
    }

    [TestMethod]
    public void NodeFor_TreeWithoutPath_UsesFallbackOrNull()
    {
        var symbol = GetSymbol("public class C { }", "C");

        Assert.AreEqual(Path.GetFullPath("/fallback/C.cs"), NodeClassifier.NodeFor(symbol, "/fallback/C.cs")?.File);
        Assert.IsNull(NodeClassifier.NodeFor(symbol)?.File);
    }
}

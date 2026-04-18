using ContextManager.Analysis.Extraction;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ContextManager.Analysis.Tests.Extraction;

[TestClass]
public class DtoDetectorTests
{
    private static TypeDeclarationSyntax ParseFirstType(string snippet)
    {
        var tree = CSharpSyntaxTree.ParseText(snippet);
        var root = tree.GetCompilationUnitRoot();
        return (TypeDeclarationSyntax)root.Members[0];
    }

    [TestMethod]
    public void BranchA_NoMethods_ReturnsTrue()
    {
        var node = ParseFirstType("class Foo { public int X { get; set; } }");
        Assert.IsTrue(DtoDetector.IsDto(node, "Foo"));
    }

    [TestMethod]
    public void BranchB_ParameterlessCtorAndAllAutoProps_ReturnsTrue()
    {
        var snippet = """
            class Foo {
                public Foo() {}
                public void DoSomething() {}
                public int X { get; set; }
                public string Y { get; init; }
            }
            """;
        var node = ParseFirstType(snippet);
        Assert.IsTrue(DtoDetector.IsDto(node, "Foo"));
    }

    [TestMethod]
    public void BranchB_PropertyWithAccessorBody_ReturnsFalse()
    {
        var snippet = """
            class Foo {
                private int _x;
                public void DoSomething() {}
                public int X { get { return _x; } set { _x = value; } }
            }
            """;
        var node = ParseFirstType(snippet);
        Assert.IsFalse(DtoDetector.IsDto(node, "Foo"));
    }

    [TestMethod]
    public void BranchB_ParameterizedCtor_ReturnsFalse()
    {
        var snippet = """
            class Foo {
                public Foo(int x) {}
                public void DoSomething() {}
                public int X { get; set; }
            }
            """;
        var node = ParseFirstType(snippet);
        Assert.IsFalse(DtoDetector.IsDto(node, "Foo"));
    }

    [DataTestMethod]
    [DataRow("CreateOrderDto")]
    [DataRow("SubmitRequest")]
    [DataRow("OrderResponse")]
    [DataRow("PlaceOrderCommand")]
    [DataRow("GetOrdersQuery")]
    [DataRow("OrderCreatedEvent")]
    [DataRow("OrderModel")]
    [DataRow("OrderViewModel")]
    public void BranchC_KnownSuffix_ReturnsTrue(string name)
    {
        // Needs a method so branch (a) doesn't fire; no parameterized ctor so branch (b) won't
        // fire either (all auto-props). Suffix alone should make it true via branch (c).
        // Use a property with a body so both (a) and (b) are false, isolating (c).
        var snippet = """
            class Placeholder {
                private int _x;
                public void DoSomething() {}
                public int X { get { return _x; } }
            }
            """;
        var node = ParseFirstType(snippet);
        Assert.IsTrue(DtoDetector.IsDto(node, name));
    }

    [TestMethod]
    public void BranchC_SuffixNotAtEnd_ReturnsFalse()
    {
        var snippet = """
            class Placeholder {
                private int _x;
                public void DoSomething() {}
                public int X { get { return _x; } }
            }
            """;
        var node = ParseFirstType(snippet);
        Assert.IsFalse(DtoDetector.IsDto(node, "ViewModelHelper"));
    }

    [TestMethod]
    public void AllBranchesFail_ReturnsFalse()
    {
        // Has a method (a=false), has a parameterized ctor (b=false), no known suffix (c=false)
        var snippet = """
            class OrderProcessor {
                private int _x;
                public OrderProcessor(int x) { _x = x; }
                public void Process() {}
                public int X { get { return _x; } }
            }
            """;
        var node = ParseFirstType(snippet);
        Assert.IsFalse(DtoDetector.IsDto(node, "OrderProcessor"));
    }
}

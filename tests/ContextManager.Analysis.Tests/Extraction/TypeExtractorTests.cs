using ContextManager.Analysis.Extraction;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ContextManager.Analysis.Tests.Extraction;

[TestClass]
public class TypeExtractorTests
{
    private static IReadOnlyList<ContextManager.Analysis.Models.TypeInfo> Walk(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetCompilationUnitRoot();
        var walker = new TypeExtractor();
        walker.Visit(root);
        return walker.Types;
    }

    // ── Single top-level class ────────────────────────────────────────────────

    [TestMethod]
    public void SingleTopLevelClass_YieldsOneTypeInfo()
    {
        var types = Walk("public class Foo { public void DoWork() {} }");

        Assert.AreEqual(1, types.Count);
        Assert.AreEqual("Foo", types[0].Name);
    }

    // ── Nested public class ───────────────────────────────────────────────────

    [TestMethod]
    public void NestedPublicClass_YieldsTwoEntries_OuterFirst()
    {
        var source = """
            public class Outer {
                public void DoWork() {}
                public class Inner {
                    public void InnerWork() {}
                }
            }
            """;

        var types = Walk(source);

        Assert.AreEqual(2, types.Count);
        Assert.AreEqual("Outer", types[0].Name);
        Assert.AreEqual("Inner", types[1].Name);
    }

    // ── Private type excluded ─────────────────────────────────────────────────

    [TestMethod]
    public void PrivateNestedClass_IsExcluded()
    {
        var source = """
            public class Outer {
                public void DoWork() {}
                private class Hidden {
                    public void HiddenWork() {}
                }
            }
            """;

        var types = Walk(source);

        Assert.AreEqual(1, types.Count);
        Assert.AreEqual("Outer", types[0].Name);
    }

    // ── All six declaration kinds ─────────────────────────────────────────────

    [TestMethod]
    public void AllSixKinds_AreCollected()
    {
        // Class has parameterized constructor to prevent DTO branch (b) from firing.
        // Struct has parameterized constructor for the same reason.
        var source = """
            public class MyClass {
                private readonly int _x;
                public MyClass(int x) { _x = x; }
                public void DoWork() {}
            }
            public interface IMyInterface { void Process(); }
            public record MyRecord(string Name);
            public struct MyStruct {
                public int Value { get; }
                public MyStruct(int v) { Value = v; }
                public void Describe() {}
            }
            public enum MyEnum { A, B }
            public delegate void MyDelegate(string arg);
            """;

        var types = Walk(source);

        Assert.AreEqual(6, types.Count);
        Assert.IsTrue(types.Any(t => t.Kind == "class" && t.Name == "MyClass"), "MyClass expected as class");
        Assert.IsTrue(types.Any(t => t.Kind == "interface" && t.Name == "IMyInterface"), "IMyInterface expected as interface");
        Assert.IsTrue(types.Any(t => t.Kind == "record" && t.Name == "MyRecord"), "MyRecord expected as record");
        Assert.IsTrue(types.Any(t => t.Kind == "struct" && t.Name == "MyStruct"), "MyStruct expected as struct");
        Assert.IsTrue(types.Any(t => t.Kind == "enum" && t.Name == "MyEnum"), "MyEnum expected as enum");
        Assert.IsTrue(types.Any(t => t.Kind == "delegate" && t.Name == "MyDelegate"), "MyDelegate expected as delegate");
    }

    // ── Source order preserved ────────────────────────────────────────────────

    [TestMethod]
    public void SourceDeclarationOrder_IsPreserved()
    {
        var source = """
            public class Alpha { public void DoWork() {} }
            public class Beta { public void DoWork() {} }
            public class Gamma { public void DoWork() {} }
            """;

        var types = Walk(source);

        Assert.AreEqual(3, types.Count);
        Assert.AreEqual("Alpha", types[0].Name);
        Assert.AreEqual("Beta", types[1].Name);
        Assert.AreEqual("Gamma", types[2].Name);
    }

    // ── Flat list (nested types not nested in output) ─────────────────────────

    [TestMethod]
    public void DeeplyNested_AreFlattenedIntoTopLevelList()
    {
        var source = """
            public class Outer {
                public void DoWork() {}
                public class Middle {
                    public void MiddleWork() {}
                    public class Deepest {
                        public void DeepWork() {}
                    }
                }
            }
            """;

        var types = Walk(source);

        Assert.AreEqual(3, types.Count);
        Assert.AreEqual("Outer", types[0].Name);
        Assert.AreEqual("Middle", types[1].Name);
        Assert.AreEqual("Deepest", types[2].Name);
    }
}

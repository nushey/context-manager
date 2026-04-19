using ContextManager.Analysis.Extraction;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ContextManager.Analysis.Tests.Extraction;

[TestClass]
public class MemberExtractorTests
{
    private static TypeDeclarationSyntax ParseFirstType(string snippet)
    {
        var tree = CSharpSyntaxTree.ParseText(snippet);
        return (TypeDeclarationSyntax)tree.GetCompilationUnitRoot().Members[0];
    }

    private static EnumDeclarationSyntax ParseFirstEnum(string snippet)
    {
        var tree = CSharpSyntaxTree.ParseText(snippet);
        return (EnumDeclarationSyntax)tree.GetCompilationUnitRoot().Members[0];
    }

    private static DelegateDeclarationSyntax ParseFirstDelegate(string snippet)
    {
        var tree = CSharpSyntaxTree.ParseText(snippet);
        return (DelegateDeclarationSyntax)tree.GetCompilationUnitRoot().Members[0];
    }

    private static EnumDeclarationSyntax ParseEnumFromFixture(string relativeFixturePath)
    {
        var fixturePath = Path.Combine(
            Path.GetDirectoryName(typeof(MemberExtractorTests).Assembly.Location)!,
            relativeFixturePath);
        var text = File.ReadAllText(fixturePath);
        var tree = CSharpSyntaxTree.ParseText(text);
        var root = tree.GetCompilationUnitRoot();

        if (root.Members[0] is FileScopedNamespaceDeclarationSyntax fileNs)
            return (EnumDeclarationSyntax)fileNs.Members[0];

        if (root.Members[0] is NamespaceDeclarationSyntax blockNs)
            return (EnumDeclarationSyntax)blockNs.Members[0];

        return (EnumDeclarationSyntax)root.Members[0];
    }

    private static TypeDeclarationSyntax ParseFromFixture(string relativeFixturePath)
    {
        var fixturePath = Path.Combine(
            Path.GetDirectoryName(typeof(MemberExtractorTests).Assembly.Location)!,
            relativeFixturePath);
        var text = File.ReadAllText(fixturePath);
        var tree = CSharpSyntaxTree.ParseText(text);
        var root = tree.GetCompilationUnitRoot();

        // Handle file-scoped namespaces: dig one level deeper
        if (root.Members[0] is FileScopedNamespaceDeclarationSyntax fileNs)
            return (TypeDeclarationSyntax)fileNs.Members[0];

        if (root.Members[0] is NamespaceDeclarationSyntax blockNs)
            return (TypeDeclarationSyntax)blockNs.Members[0];

        return (TypeDeclarationSyntax)root.Members[0];
    }

    // ── Class with DI constructor ─────────────────────────────────────────────

    [TestMethod]
    public void Class_DiConstructor_PopulatesConstructorDependencies()
    {
        var node = ParseFromFixture(@"Fixtures/ServiceWithDependencies.cs");
        var result = MemberExtractor.Build(node, isTopLevel: true);

        Assert.AreEqual(2, result.ConstructorDependencies.Count);
        Assert.AreEqual("IOrderRepository", result.ConstructorDependencies[0].Type);
        Assert.AreEqual("orderRepository", result.ConstructorDependencies[0].Name);
        Assert.AreEqual("IEventBus", result.ConstructorDependencies[1].Type);
        Assert.AreEqual("eventBus", result.ConstructorDependencies[1].Name);
    }

    [TestMethod]
    public void Class_ConstructorNotListedInMethods()
    {
        var node = ParseFromFixture(@"Fixtures/ServiceWithDependencies.cs");
        var result = MemberExtractor.Build(node, isTopLevel: true);

        Assert.IsFalse(result.Methods.Any(m => m.Name == "ServiceWithDependencies"));
    }

    [TestMethod]
    public void Class_PrivateMethodsExcluded()
    {
        var node = ParseFromFixture(@"Fixtures/ServiceWithDependencies.cs");
        var result = MemberExtractor.Build(node, isTopLevel: true);

        Assert.IsFalse(result.Methods.Any(m => m.Name == "InternalHelper"));
    }

    [TestMethod]
    public void Class_PublicMethodsIncluded_WithAttributes()
    {
        var node = ParseFromFixture(@"Fixtures/ServiceWithDependencies.cs");
        var result = MemberExtractor.Build(node, isTopLevel: true);

        Assert.IsTrue(result.Methods.Any(m => m.Name == "ProcessOrder"));
        var processOrder = result.Methods.First(m => m.Name == "ProcessOrder");
        Assert.AreEqual("string", processOrder.ReturnType);
        Assert.IsTrue(processOrder.Attributes.Any(a => a.Contains("Authorize")));
    }

    [TestMethod]
    public void Class_KindIsClass()
    {
        var node = ParseFromFixture(@"Fixtures/ServiceWithDependencies.cs");
        var result = MemberExtractor.Build(node, isTopLevel: true);

        Assert.AreEqual("class", result.Kind);
    }

    [TestMethod]
    public void Class_PublicPropertyIncluded()
    {
        var node = ParseFromFixture(@"Fixtures/ServiceWithDependencies.cs");
        var result = MemberExtractor.Build(node, isTopLevel: true);

        Assert.IsTrue(result.Properties.Any(p => p.Name == "OrderCount"));
    }

    // ── Public const and static readonly fields ───────────────────────────────

    [TestMethod]
    public void Class_PublicConst_LandsInProperties()
    {
        var snippet = """
            class Config {
                public const string DefaultName = "default";
                public Config(string name) {}
                public void DoWork() {}
            }
            """;
        var node = ParseFirstType(snippet);
        var result = MemberExtractor.Build(node, isTopLevel: true);

        var prop = result.Properties.FirstOrDefault(p => p.Name == "DefaultName");
        Assert.IsNotNull(prop);
        Assert.AreEqual("string", prop.Type);
        Assert.AreEqual("public", prop.Access);
    }

    [TestMethod]
    public void Class_PublicStaticReadonly_LandsInProperties()
    {
        var snippet = """
            class Config {
                public static readonly int MaxRetries = 3;
                public Config(int maxRetries) {}
                public void DoWork() {}
            }
            """;
        var node = ParseFirstType(snippet);
        var result = MemberExtractor.Build(node, isTopLevel: true);

        var prop = result.Properties.FirstOrDefault(p => p.Name == "MaxRetries");
        Assert.IsNotNull(prop);
        Assert.AreEqual("int", prop.Type);
        Assert.AreEqual("public", prop.Access);
    }

    [TestMethod]
    public void Class_PrivateField_NotInProperties()
    {
        var snippet = """
            class Service {
                private int _counter;
                public void DoWork() {}
            }
            """;
        var node = ParseFirstType(snippet);
        var result = MemberExtractor.Build(node, isTopLevel: true);

        Assert.IsFalse(result.Properties?.Any(p => p.Name == "_counter") == true);
    }

    // ── Interface ─────────────────────────────────────────────────────────────

    [TestMethod]
    public void Interface_KindIsInterface()
    {
        var snippet = """
            interface IOrderService {
                string Process(int id);
            }
            """;
        var node = ParseFirstType(snippet);
        var result = MemberExtractor.Build(node, isTopLevel: true);

        Assert.AreEqual("interface", result.Kind);
    }

    [TestMethod]
    public void Interface_EmptyConstructorDependencies()
    {
        var snippet = """
            interface IOrderService {
                string Process(int id);
            }
            """;
        var node = ParseFirstType(snippet);
        var result = MemberExtractor.Build(node, isTopLevel: true);

        Assert.IsNull(result.ConstructorDependencies);
    }

    [TestMethod]
    public void Interface_MethodsIncluded()
    {
        var snippet = """
            interface IOrderService {
                string Process(int id);
                void Cancel(int id);
            }
            """;
        var node = ParseFirstType(snippet);
        var result = MemberExtractor.Build(node, isTopLevel: true);

        Assert.AreEqual(2, result.Methods.Count);
        Assert.AreEqual("Process", result.Methods[0].Name);
        Assert.AreEqual("Cancel", result.Methods[1].Name);
    }

    [TestMethod]
    public void Method_LineNumbers_AreCorrect()
    {
        var snippet = """
            class Service {
                public void First() {}
                public void Second() {}
            }
            """;
        var node = ParseFirstType(snippet);
        var result = MemberExtractor.Build(node, isTopLevel: true);

        var first = result.Methods![0];
        var second = result.Methods![1];

        Assert.AreEqual(2, first.StartLine);
        Assert.AreEqual(2, first.EndLine);
        Assert.AreEqual(3, second.StartLine);
        Assert.AreEqual(3, second.EndLine);
    }

    [TestMethod]
    public void Method_MultiLineMethod_StartAndEndDiffer()
    {
        var snippet = """
            class Service {
                public void DoWork()
                {
                    var x = 1;
                }
            }
            """;
        var node = ParseFirstType(snippet);
        var result = MemberExtractor.Build(node, isTopLevel: true);

        var method = result.Methods![0];
        Assert.IsTrue(method.EndLine > method.StartLine);
    }

    [TestMethod]
    public void Interface_AllBaseEntriesAreImplements()
    {
        var snippet = """
            interface IOrderService : IBaseService, IDisposable {
                void Process();
            }
            """;
        var node = ParseFirstType(snippet);
        var result = MemberExtractor.Build(node, isTopLevel: true);

        Assert.IsNull(result.Base);
        Assert.AreEqual(2, result.Implements.Count);
        Assert.IsTrue(result.Implements.Contains("IBaseService"));
        Assert.IsTrue(result.Implements.Contains("IDisposable"));
    }

    // ── Struct ────────────────────────────────────────────────────────────────

    [TestMethod]
    public void Struct_KindIsStruct()
    {
        var snippet = """
            struct Money {
                public decimal Amount { get; }
                public string Currency { get; }
                public Money(decimal amount, string currency) { Amount = amount; Currency = currency; }
                public override string ToString() => $"{Amount} {Currency}";
            }
            """;
        var node = ParseFirstType(snippet);
        var result = MemberExtractor.Build(node, isTopLevel: true);

        Assert.AreEqual("struct", result.Kind);
    }

    [TestMethod]
    public void Struct_ConstructorDependenciesPopulated()
    {
        var snippet = """
            struct Money {
                public decimal Amount { get; }
                public string Currency { get; }
                public Money(decimal amount, string currency) { Amount = amount; Currency = currency; }
                public override string ToString() => $"{Amount} {Currency}";
            }
            """;
        var node = ParseFirstType(snippet);
        var result = MemberExtractor.Build(node, isTopLevel: true);

        Assert.AreEqual(2, result.ConstructorDependencies.Count);
        Assert.AreEqual("decimal", result.ConstructorDependencies[0].Type);
        Assert.AreEqual("amount", result.ConstructorDependencies[0].Name);
    }

    // ── Record ────────────────────────────────────────────────────────────────

    [TestMethod]
    public void Record_KindIsRecord()
    {
        var snippet = """
            record CreateOrderRecord(string CustomerId, decimal Total)
            {
                public string Describe() => $"{CustomerId}: {Total}";
            }
            """;
        var node = ParseFirstType(snippet);
        var result = MemberExtractor.Build(node, isTopLevel: true);

        Assert.AreEqual("record", result.Kind);
    }

    [TestMethod]
    public void Record_PrimaryConstructorPopulatesConstructorDependencies()
    {
        var snippet = """
            record CreateOrderRecord(string CustomerId, decimal Total)
            {
                public string Describe() => $"{CustomerId}: {Total}";
            }
            """;
        var node = ParseFirstType(snippet);
        var result = MemberExtractor.Build(node, isTopLevel: true);

        Assert.AreEqual(2, result.ConstructorDependencies.Count);
        Assert.AreEqual("string", result.ConstructorDependencies[0].Type);
        Assert.AreEqual("CustomerId", result.ConstructorDependencies[0].Name);
        Assert.AreEqual("decimal", result.ConstructorDependencies[1].Type);
        Assert.AreEqual("Total", result.ConstructorDependencies[1].Name);
    }

    // ── DTO ───────────────────────────────────────────────────────────────────

    [TestMethod]
    public void Dto_SuffixMatch_KindIsDto()
    {
        var snippet = """
            class CreateOrderRequest {
                private int _x;
                public void DoSomething() {}
                public int X { get { return _x; } }
            }
            """;
        var node = ParseFirstType(snippet);
        var result = MemberExtractor.Build(node, isTopLevel: true);

        Assert.AreEqual("dto", result.Kind);
    }

    [TestMethod]
    public void Dto_SuffixMatch_PropertiesEmpty()
    {
        var snippet = """
            class CreateOrderRequest {
                private int _x;
                public void DoSomething() {}
                public int X { get { return _x; } }
            }
            """;
        var node = ParseFirstType(snippet);
        var result = MemberExtractor.Build(node, isTopLevel: true);

        Assert.IsNull(result.Properties);
    }

    // ── Declaration order ─────────────────────────────────────────────────────

    [TestMethod]
    public void Class_DeclarationOrderPreservedInMethods()
    {
        var snippet = """
            class OrderService {
                public OrderService(IRepo repo) {}
                public void First() {}
                public void Second() {}
                public void Third() {}
            }
            """;
        var node = ParseFirstType(snippet);
        var result = MemberExtractor.Build(node, isTopLevel: true);

        Assert.AreEqual(3, result.Methods.Count);
        Assert.AreEqual("First", result.Methods[0].Name);
        Assert.AreEqual("Second", result.Methods[1].Name);
        Assert.AreEqual("Third", result.Methods[2].Name);
    }

    [TestMethod]
    public void Class_DeclarationOrderPreservedInProperties()
    {
        var snippet = """
            class Config {
                public int Alpha { get; set; }
                public int Beta { get; set; }
                public Config(int x) {}
                public void DoWork() {}
            }
            """;
        var node = ParseFirstType(snippet);
        var result = MemberExtractor.Build(node, isTopLevel: true);

        Assert.AreEqual(2, result.Properties.Count);
        Assert.AreEqual("Alpha", result.Properties[0].Name);
        Assert.AreEqual("Beta", result.Properties[1].Name);
    }

    // ── Base / Implements ─────────────────────────────────────────────────────

    [TestMethod]
    public void Class_FirstNonInterfaceEntryIsBase()
    {
        var snippet = """
            class OrderService : BaseService, IOrderService {
                public void Process() {}
            }
            """;
        var node = ParseFirstType(snippet);
        var result = MemberExtractor.Build(node, isTopLevel: true);

        Assert.AreEqual("BaseService", result.Base);
        Assert.AreEqual(1, result.Implements.Count);
        Assert.AreEqual("IOrderService", result.Implements[0]);
    }

    [TestMethod]
    public void Class_InterfaceFirstEntry_AllAreImplements()
    {
        var snippet = """
            class OrderService : IOrderService, IDisposable {
                public void Process() {}
            }
            """;
        var node = ParseFirstType(snippet);
        var result = MemberExtractor.Build(node, isTopLevel: true);

        Assert.IsNull(result.Base);
        Assert.AreEqual(2, result.Implements.Count);
    }

    // ── Members null for non-enums ─────────────────────────────────────────────

    [TestMethod]
    public void Class_MembersIsNull()
    {
        var snippet = """
            class Foo {
                public void DoWork() {}
            }
            """;
        var node = ParseFirstType(snippet);
        var result = MemberExtractor.Build(node, isTopLevel: true);

        Assert.IsNull(result.Members);
    }

    // ── Enum ──────────────────────────────────────────────────────────────────

    [TestMethod]
    public void Enum_KindIsEnum()
    {
        var node = ParseEnumFromFixture(@"Fixtures/OrderStatus.cs");
        var result = MemberExtractor.Build(node, isTopLevel: true);

        Assert.AreEqual("enum", result.Kind);
    }

    [TestMethod]
    public void Enum_MembersMatchSourceOrder()
    {
        var node = ParseEnumFromFixture(@"Fixtures/OrderStatus.cs");
        var result = MemberExtractor.Build(node, isTopLevel: true);

        Assert.IsNotNull(result.Members);
        Assert.AreEqual(3, result.Members.Count);
        Assert.AreEqual("Pending", result.Members[0]);
        Assert.AreEqual("Processing", result.Members[1]);
        Assert.AreEqual("Completed", result.Members[2]);
    }

    [TestMethod]
    public void Enum_MembersContainNoNumericValues()
    {
        var node = ParseFirstEnum("enum Status { Active = 1, Inactive = 2 }");
        var result = MemberExtractor.Build(node, isTopLevel: true);

        Assert.IsNotNull(result.Members);
        Assert.IsTrue(result.Members.All(m => !m.Contains('=')));
        CollectionAssert.AreEqual(new[] { "Active", "Inactive" }, result.Members.ToArray());
    }

    [TestMethod]
    public void Enum_MethodsIsEmpty()
    {
        var node = ParseEnumFromFixture(@"Fixtures/OrderStatus.cs");
        var result = MemberExtractor.Build(node, isTopLevel: true);

        Assert.IsNull(result.Methods);
    }

    [TestMethod]
    public void Enum_PropertiesIsEmpty()
    {
        var node = ParseEnumFromFixture(@"Fixtures/OrderStatus.cs");
        var result = MemberExtractor.Build(node, isTopLevel: true);

        Assert.IsNull(result.Properties);
    }

    [TestMethod]
    public void Enum_ConstructorDependenciesIsEmpty()
    {
        var node = ParseEnumFromFixture(@"Fixtures/OrderStatus.cs");
        var result = MemberExtractor.Build(node, isTopLevel: true);

        Assert.IsNull(result.ConstructorDependencies);
    }

    // ── Delegate ──────────────────────────────────────────────────────────────

    [TestMethod]
    public void Delegate_KindIsDelegate()
    {
        var node = ParseFirstDelegate("public delegate string OrderHandler(int orderId, string status);");
        var result = MemberExtractor.Build(node, isTopLevel: true);

        Assert.AreEqual("delegate", result.Kind);
    }

    [TestMethod]
    public void Delegate_MembersIsNull()
    {
        var node = ParseFirstDelegate("public delegate void Notify(string message);");
        var result = MemberExtractor.Build(node, isTopLevel: true);

        Assert.IsNull(result.Members);
    }

    [TestMethod]
    public void Delegate_SingleSyntheticMethod()
    {
        var node = ParseFirstDelegate("public delegate string OrderHandler(int orderId, string status);");
        var result = MemberExtractor.Build(node, isTopLevel: true);

        Assert.AreEqual(1, result.Methods.Count);
    }

    [TestMethod]
    public void Delegate_SyntheticMethodHasDelegateName()
    {
        var node = ParseFirstDelegate("public delegate string OrderHandler(int orderId, string status);");
        var result = MemberExtractor.Build(node, isTopLevel: true);

        Assert.AreEqual("OrderHandler", result.Methods[0].Name);
    }

    [TestMethod]
    public void Delegate_SyntheticMethodHasCorrectReturnType()
    {
        var node = ParseFirstDelegate("public delegate string OrderHandler(int orderId, string status);");
        var result = MemberExtractor.Build(node, isTopLevel: true);

        Assert.AreEqual("string", result.Methods[0].ReturnType);
    }

    [TestMethod]
    public void Delegate_SyntheticMethodParametersPreservedInOrder()
    {
        var node = ParseFirstDelegate("public delegate string OrderHandler(int orderId, string status);");
        var result = MemberExtractor.Build(node, isTopLevel: true);

        var parameters = result.Methods![0].Parameters!;
        Assert.AreEqual(2, parameters.Count);
        Assert.AreEqual("int", parameters[0].Type);
        Assert.AreEqual("orderId", parameters[0].Name);
        Assert.AreEqual("string", parameters[1].Type);
        Assert.AreEqual("status", parameters[1].Name);
    }

    [TestMethod]
    public void Delegate_PropertiesIsEmpty()
    {
        var node = ParseFirstDelegate("public delegate void Notify(string message);");
        var result = MemberExtractor.Build(node, isTopLevel: true);

        Assert.IsNull(result.Properties);
    }

    [TestMethod]
    public void Delegate_ConstructorDependenciesIsEmpty()
    {
        var node = ParseFirstDelegate("public delegate void Notify(string message);");
        var result = MemberExtractor.Build(node, isTopLevel: true);

        Assert.IsNull(result.ConstructorDependencies);
    }
}

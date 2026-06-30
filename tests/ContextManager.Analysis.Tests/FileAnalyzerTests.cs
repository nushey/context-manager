using System.Text.Json;
using ContextManager.Analysis;
using ContextManager.Analysis.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ContextManager.Analysis.Tests;

[TestClass]
public class FileAnalyzerTests
{
    private static readonly FileAnalyzer _analyzer = new();

    private static string FixturePath(string name)
        => Path.Combine(
            Path.GetDirectoryName(typeof(FileAnalyzerTests).Assembly.Location)!,
            "Fixtures",
            name);

    // ── Error paths ──────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Analyze_NonExistentPath_ReturnsFileNotFound()
    {
        var result = await _analyzer.AnalyzeAsync("/does/not/exist/foo.cs", CancellationToken.None);

        Assert.IsNotNull(result.Error);
        Assert.AreEqual("file_not_found", result.Error!.Code);
        Assert.AreEqual("/does/not/exist/foo.cs", result.Error.FilePath);
    }

    [TestMethod]
    public async Task Analyze_NonCsExtension_ReturnsNotACsFile()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            var txtPath = Path.ChangeExtension(tmp, ".txt");
            File.Move(tmp, txtPath);
            var result = await _analyzer.AnalyzeAsync(txtPath, CancellationToken.None);

            Assert.IsNotNull(result.Error);
            Assert.AreEqual("not_a_cs_file", result.Error!.Code);
            Assert.AreEqual(txtPath, result.Error.FilePath);
        }
        finally
        {
            var txtPath = Path.ChangeExtension(tmp, ".txt");
            if (File.Exists(txtPath)) File.Delete(txtPath);
        }
    }

    [TestMethod]
    public async Task Analyze_UnparseableCSharp_ReturnsFileAnalysisWithParseErrors()
    {
        // Best-effort: a malformed file yields a FileAnalysis (salvaging whatever Roslyn parsed)
        // with a populated parseErrors list, not an AnalysisError — unless the syntax root itself
        // is unobtainable.
        var tmp = Path.ChangeExtension(Path.GetTempFileName(), ".cs");
        try
        {
            File.WriteAllText(tmp, "class Broken { public void Foo( { }");
            var result = await _analyzer.AnalyzeAsync(tmp, CancellationToken.None);

            Assert.IsNotNull(result.Analysis);
            var analysis = result.Analysis!;
            Assert.IsNotNull(analysis.ParseErrors);
            Assert.IsTrue(analysis.ParseErrors!.Count > 0);
            Assert.AreEqual(Path.GetFileName(tmp), analysis.File);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    [TestMethod]
    public async Task Analyze_CleanFile_ParseErrorsIsNull()
    {
        var result = await _analyzer.AnalyzeAsync(FixturePath("OrderStatus.cs"), CancellationToken.None);

        Assert.IsNotNull(result.Analysis);
        Assert.IsNull(result.Analysis!.ParseErrors);
    }

    // ── Namespace: file-scoped ───────────────────────────────────────────────

    [TestMethod]
    public async Task Analyze_FileScopedNamespace_PopulatesNamespace()
    {
        var result = await _analyzer.AnalyzeAsync(FixturePath("OrderStatus.cs"), CancellationToken.None);

        Assert.IsNotNull(result.Analysis);
        Assert.AreEqual("ContextManager.Analysis.Tests.Fixtures", result.Analysis!.Namespace);
    }

    // ── Namespace: classic block ──────────────────────────────────────────────

    [TestMethod]
    public async Task Analyze_ClassicBlockNamespace_PopulatesNamespace()
    {
        var tmp = Path.ChangeExtension(Path.GetTempFileName(), ".cs");
        try
        {
            File.WriteAllText(tmp, """
                namespace My.Block.Namespace
                {
                    public class Foo { }
                }
                """);
            var result = await _analyzer.AnalyzeAsync(tmp, CancellationToken.None);

            Assert.IsNotNull(result.Analysis);
            Assert.AreEqual("My.Block.Namespace", result.Analysis!.Namespace);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    // ── Usings ───────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Analyze_ServiceWithDependencies_UsingsInDeclarationOrder()
    {
        var result = await _analyzer.AnalyzeAsync(FixturePath("ServiceWithDependencies.cs"), CancellationToken.None);

        Assert.IsNotNull(result.Analysis);
        var analysis = result.Analysis!;
        Assert.AreEqual(1, analysis.Usings.Count);
        Assert.AreEqual("System.Threading", analysis.Usings[0]);
    }

    // ── ServiceWithDependencies ───────────────────────────────────────────────

    [TestMethod]
    public async Task Analyze_ServiceWithDependencies_ConstructorDependencies()
    {
        var result = await _analyzer.AnalyzeAsync(FixturePath("ServiceWithDependencies.cs"), CancellationToken.None);

        Assert.IsNotNull(result.Analysis);
        var analysis = result.Analysis!;
        var type = analysis.Types.First(t => t.Name == "ServiceWithDependencies");
        Assert.AreEqual(2, type.ConstructorDependencies.Count);
        Assert.AreEqual("IOrderRepository", type.ConstructorDependencies[0].Type);
        Assert.AreEqual("orderRepository", type.ConstructorDependencies[0].Name);
        Assert.AreEqual("IEventBus", type.ConstructorDependencies[1].Type);
        Assert.AreEqual("eventBus", type.ConstructorDependencies[1].Name);
    }

    [TestMethod]
    public async Task Analyze_PrimaryCtorClass_ConstructorDependencies()
    {
        var result = await _analyzer.AnalyzeAsync(FixturePath("PrimaryCtorService.cs"), CancellationToken.None);

        Assert.IsNotNull(result.Analysis);
        var analysis = result.Analysis!;
        var type = analysis.Types.First(t => t.Name == "PrimaryCtorService");
        Assert.IsNotNull(type.ConstructorDependencies);
        Assert.AreEqual(2, type.ConstructorDependencies!.Count);
        Assert.AreEqual("IOrderRepository", type.ConstructorDependencies[0].Type);
        Assert.AreEqual("orderRepository", type.ConstructorDependencies[0].Name);
        Assert.AreEqual("IEventBus", type.ConstructorDependencies[1].Type);
        Assert.AreEqual("eventBus", type.ConstructorDependencies[1].Name);
    }

    [TestMethod]
    public async Task Analyze_ServiceWithDependencies_MethodAttributes()
    {
        var result = await _analyzer.AnalyzeAsync(FixturePath("ServiceWithDependencies.cs"), CancellationToken.None);

        Assert.IsNotNull(result.Analysis);
        var analysis = result.Analysis!;
        var type = analysis.Types.First(t => t.Name == "ServiceWithDependencies");
        var processOrder = type.Methods.First(m => m.Name == "ProcessOrder");
        Assert.IsTrue(processOrder.Attributes.Any(a => a.Contains("Authorize")));
    }

    [TestMethod]
    public async Task Analyze_ServiceWithDependencies_PrivateMethodExcluded()
    {
        var result = await _analyzer.AnalyzeAsync(FixturePath("ServiceWithDependencies.cs"), CancellationToken.None);

        Assert.IsNotNull(result.Analysis);
        var analysis = result.Analysis!;
        var type = analysis.Types.First(t => t.Name == "ServiceWithDependencies");
        Assert.IsFalse(type.Methods.Any(m => m.Name == "InternalHelper"));
    }

    // ── OrderServiceInterface ─────────────────────────────────────────────────

    [TestMethod]
    public async Task Analyze_OrderServiceInterface_KindIsInterface()
    {
        var result = await _analyzer.AnalyzeAsync(FixturePath("OrderServiceInterface.cs"), CancellationToken.None);

        Assert.IsNotNull(result.Analysis);
        var analysis = result.Analysis!;
        var type = analysis.Types.First(t => t.Name == "IOrderService");
        Assert.AreEqual("interface", type.Kind);
    }

    [TestMethod]
    public async Task Analyze_OrderServiceInterface_MethodList()
    {
        var result = await _analyzer.AnalyzeAsync(FixturePath("OrderServiceInterface.cs"), CancellationToken.None);

        Assert.IsNotNull(result.Analysis);
        var analysis = result.Analysis!;
        var type = analysis.Types.First(t => t.Name == "IOrderService");
        Assert.AreEqual(2, type.Methods.Count);
        Assert.AreEqual("Process", type.Methods[0].Name);
        Assert.AreEqual("Cancel", type.Methods[1].Name);
    }

    // ── CreateOrderRecord ─────────────────────────────────────────────────────

    [TestMethod]
    public async Task Analyze_CreateOrderRecord_KindIsRecord()
    {
        var result = await _analyzer.AnalyzeAsync(FixturePath("CreateOrderRecord.cs"), CancellationToken.None);

        Assert.IsNotNull(result.Analysis);
        var analysis = result.Analysis!;
        var type = analysis.Types.First(t => t.Name == "CreateOrderRecord");
        Assert.AreEqual("record", type.Kind);
    }

    [TestMethod]
    public async Task Analyze_CreateOrderRecord_PrimaryConstructorInConstructorDependencies()
    {
        var result = await _analyzer.AnalyzeAsync(FixturePath("CreateOrderRecord.cs"), CancellationToken.None);

        Assert.IsNotNull(result.Analysis);
        var analysis = result.Analysis!;
        var type = analysis.Types.First(t => t.Name == "CreateOrderRecord");
        Assert.AreEqual(3, type.ConstructorDependencies.Count);
        Assert.AreEqual("string", type.ConstructorDependencies[0].Type);
        Assert.AreEqual("CustomerId", type.ConstructorDependencies[0].Name);
        Assert.AreEqual("decimal", type.ConstructorDependencies[1].Type);
        Assert.AreEqual("Total", type.ConstructorDependencies[1].Name);
        Assert.AreEqual("string", type.ConstructorDependencies[2].Type);
        Assert.AreEqual("ShippingAddress", type.ConstructorDependencies[2].Name);
    }

    [TestMethod]
    public async Task Analyze_CreateOrderRecord_PrimaryConstructorNotInMethods()
    {
        var result = await _analyzer.AnalyzeAsync(FixturePath("CreateOrderRecord.cs"), CancellationToken.None);

        Assert.IsNotNull(result.Analysis);
        var analysis = result.Analysis!;
        var type = analysis.Types.First(t => t.Name == "CreateOrderRecord");
        Assert.IsFalse(type.Methods?.Any(m => m.Name == "CreateOrderRecord") == true);
    }

    // ── Money (struct) ────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Analyze_Money_KindIsStruct()
    {
        var result = await _analyzer.AnalyzeAsync(FixturePath("Money.cs"), CancellationToken.None);

        Assert.IsNotNull(result.Analysis);
        var analysis = result.Analysis!;
        var type = analysis.Types.First(t => t.Name == "Money");
        Assert.AreEqual("struct", type.Kind);
    }

    [TestMethod]
    public async Task Analyze_Money_PropertiesPopulated()
    {
        var result = await _analyzer.AnalyzeAsync(FixturePath("Money.cs"), CancellationToken.None);

        Assert.IsNotNull(result.Analysis);
        var analysis = result.Analysis!;
        var type = analysis.Types.First(t => t.Name == "Money");
        Assert.AreEqual(2, type.Properties.Count);
        Assert.AreEqual("Amount", type.Properties[0].Name);
        Assert.AreEqual("Currency", type.Properties[1].Name);
    }

    // ── OrderStatus (enum) ────────────────────────────────────────────────────

    [TestMethod]
    public async Task Analyze_OrderStatus_KindIsEnum()
    {
        var result = await _analyzer.AnalyzeAsync(FixturePath("OrderStatus.cs"), CancellationToken.None);

        Assert.IsNotNull(result.Analysis);
        var analysis = result.Analysis!;
        var type = analysis.Types.First(t => t.Name == "OrderStatus");
        Assert.AreEqual("enum", type.Kind);
    }

    [TestMethod]
    public async Task Analyze_OrderStatus_MembersNamesOnly()
    {
        var result = await _analyzer.AnalyzeAsync(FixturePath("OrderStatus.cs"), CancellationToken.None);

        Assert.IsNotNull(result.Analysis);
        var analysis = result.Analysis!;
        var type = analysis.Types.First(t => t.Name == "OrderStatus");
        Assert.IsNotNull(type.Members);
        CollectionAssert.AreEqual(
            new[] { "Pending", "Processing", "Completed" },
            type.Members!.ToArray());
    }

    // ── DTO branch (a): no methods ────────────────────────────────────────────

    [TestMethod]
    public async Task Analyze_DtoByNoMethods_KindIsDto()
    {
        var result = await _analyzer.AnalyzeAsync(FixturePath("DtoByNoMethods.cs"), CancellationToken.None);

        Assert.IsNotNull(result.Analysis);
        var analysis = result.Analysis!;
        var type = analysis.Types.First(t => t.Name == "OrderSummary");
        Assert.AreEqual("dto", type.Kind);
    }

    [TestMethod]
    public async Task Analyze_DtoByNoMethods_PropertiesEmpty()
    {
        var result = await _analyzer.AnalyzeAsync(FixturePath("DtoByNoMethods.cs"), CancellationToken.None);

        Assert.IsNotNull(result.Analysis);
        var analysis = result.Analysis!;
        var type = analysis.Types.First(t => t.Name == "OrderSummary");
        Assert.IsNull(type.Properties);
    }

    // ── DTO branch (b): parameterless ctor + auto-properties ─────────────────

    [TestMethod]
    public async Task Analyze_DtoByAutoProperties_KindIsDto()
    {
        var result = await _analyzer.AnalyzeAsync(FixturePath("DtoByAutoProperties.cs"), CancellationToken.None);

        Assert.IsNotNull(result.Analysis);
        var analysis = result.Analysis!;
        var type = analysis.Types.First(t => t.Name == "CustomerInfo");
        Assert.AreEqual("dto", type.Kind);
    }

    [TestMethod]
    public async Task Analyze_DtoByAutoProperties_PropertiesEmpty()
    {
        var result = await _analyzer.AnalyzeAsync(FixturePath("DtoByAutoProperties.cs"), CancellationToken.None);

        Assert.IsNotNull(result.Analysis);
        var analysis = result.Analysis!;
        var type = analysis.Types.First(t => t.Name == "CustomerInfo");
        Assert.IsNull(type.Properties);
    }

    // ── DTO branch (c): suffix-based ──────────────────────────────────────────

    [TestMethod]
    public async Task Analyze_CreateOrderRequest_KindIsDto()
    {
        var result = await _analyzer.AnalyzeAsync(FixturePath("CreateOrderRequest.cs"), CancellationToken.None);

        Assert.IsNotNull(result.Analysis);
        var analysis = result.Analysis!;
        var type = analysis.Types.First(t => t.Name == "CreateOrderRequest");
        Assert.AreEqual("dto", type.Kind);
    }

    [TestMethod]
    public async Task Analyze_CreateOrderRequest_PropertiesEmpty()
    {
        var result = await _analyzer.AnalyzeAsync(FixturePath("CreateOrderRequest.cs"), CancellationToken.None);

        Assert.IsNotNull(result.Analysis);
        var analysis = result.Analysis!;
        var type = analysis.Types.First(t => t.Name == "CreateOrderRequest");
        Assert.IsNull(type.Properties);
    }

    // ── Generic types: name with type parameters + type-level constraints ────

    [TestMethod]
    public async Task Analyze_GenericClass_NameIncludesTypeParameters()
    {
        var result = await _analyzer.AnalyzeAsync(FixturePath("GenericTypes.cs"), CancellationToken.None);

        Assert.IsNotNull(result.Analysis);
        var analysis = result.Analysis!;
        Assert.IsTrue(analysis.Types.Any(t => t.Name == "GenericRepository<TEntity>"),
            $"Expected 'GenericRepository<TEntity>'. Got: [{string.Join(", ", analysis.Types.Select(t => t.Name))}]");
        Assert.IsTrue(analysis.Types.Any(t => t.Name == "IRepository<TEntity>"));
    }

    [TestMethod]
    public async Task Analyze_GenericClass_TypeLevelGenericConstraints()
    {
        var result = await _analyzer.AnalyzeAsync(FixturePath("GenericTypes.cs"), CancellationToken.None);

        Assert.IsNotNull(result.Analysis);
        var analysis = result.Analysis!;
        var type = analysis.Types.First(t => t.Name == "GenericRepository<TEntity>");
        Assert.IsNotNull(type.GenericConstraints);
        Assert.AreEqual(1, type.GenericConstraints!.Count);
        Assert.AreEqual("where TEntity : class, IEntity, new()", type.GenericConstraints[0]);
    }

    [TestMethod]
    public async Task Analyze_GenericClass_MultipleConstraintClausesInDeclarationOrder()
    {
        var result = await _analyzer.AnalyzeAsync(FixturePath("GenericTypes.cs"), CancellationToken.None);

        Assert.IsNotNull(result.Analysis);
        var analysis = result.Analysis!;
        var type = analysis.Types.First(t => t.Name == "EntityMapper<TSource, TResult>");
        Assert.IsNotNull(type.GenericConstraints);
        Assert.AreEqual(2, type.GenericConstraints!.Count);
        Assert.AreEqual("where TSource : notnull", type.GenericConstraints[0]);
        Assert.AreEqual("where TResult : class", type.GenericConstraints[1]);
    }

    [TestMethod]
    public async Task Analyze_NonGenericType_NameUnchangedAndConstraintsNull()
    {
        var result = await _analyzer.AnalyzeAsync(FixturePath("ServiceWithDependencies.cs"), CancellationToken.None);

        Assert.IsNotNull(result.Analysis);
        var analysis = result.Analysis!;
        var type = analysis.Types.First(t => t.Name == "ServiceWithDependencies");
        Assert.IsNull(type.GenericConstraints);
    }

    [TestMethod]
    public async Task Analyze_GenericDtoSuffix_BareIdentifierDrivesDtoHeuristic()
    {
        var result = await _analyzer.AnalyzeAsync(FixturePath("GenericTypes.cs"), CancellationToken.None);

        Assert.IsNotNull(result.Analysis);
        var analysis = result.Analysis!;
        var type = analysis.Types.First(t => t.Name == "PagedResponse<T>");
        Assert.AreEqual("dto", type.Kind);
    }

    // ── Method modifiers + property accessors ─────────────────────────────────

    private static async Task<TypeInfo> AnalyzeMemberDetailShowcase()
    {
        var result = await _analyzer.AnalyzeAsync(FixturePath("MemberDetailShowcase.cs"), CancellationToken.None);
        Assert.IsNotNull(result.Analysis);
        return result.Analysis!.Types.First(t => t.Name == "MemberDetailShowcase");
    }

    [TestMethod]
    public async Task Analyze_MethodModifiers_NonAccessModifiersInDeclarationOrder()
    {
        var type = await AnalyzeMemberDetailShowcase();

        CollectionAssert.AreEqual(new[] { "async" },
            type.Methods!.First(m => m.Name == "LoadAsync").Modifiers!.ToArray());
        CollectionAssert.AreEqual(new[] { "static", "async" },
            type.Methods!.First(m => m.Name == "CountAsync").Modifiers!.ToArray());
        CollectionAssert.AreEqual(new[] { "override" },
            type.Methods!.First(m => m.Name == "ToString").Modifiers!.ToArray());
        CollectionAssert.AreEqual(new[] { "virtual" },
            type.Methods!.First(m => m.Name == "Extend").Modifiers!.ToArray());
        CollectionAssert.AreEqual(new[] { "abstract" },
            type.Methods!.First(m => m.Name == "MustImplement").Modifiers!.ToArray());
    }

    [TestMethod]
    public async Task Analyze_MethodWithoutNonAccessModifiers_ModifiersNull()
    {
        var type = await AnalyzeMemberDetailShowcase();

        Assert.IsNull(type.Methods!.First(m => m.Name == "Plain").Modifiers);
    }

    [TestMethod]
    public async Task Analyze_PropertyAccessors_RenderedInCSharpSyntax()
    {
        var type = await AnalyzeMemberDetailShowcase();

        Assert.AreEqual("get; set;", type.Properties!.First(p => p.Name == "GetSet").Accessors);
        Assert.AreEqual("get; init;", type.Properties!.First(p => p.Name == "GetInit").Accessors);
        Assert.AreEqual("get; private set;", type.Properties!.First(p => p.Name == "GetPrivateSet").Accessors);
    }

    [TestMethod]
    public async Task Analyze_ExpressionBodiedProperty_AccessorsIsGetOnly()
    {
        var type = await AnalyzeMemberDetailShowcase();

        Assert.AreEqual("get;", type.Properties!.First(p => p.Name == "ExpressionBodied").Accessors);
    }

    // ── Explicit interface implementations ────────────────────────────────────

    [TestMethod]
    public async Task Analyze_ExplicitInterfaceMethod_QualifiedNameAndPublicAccess()
    {
        var result = await _analyzer.AnalyzeAsync(FixturePath("ExplicitContractService.cs"), CancellationToken.None);

        Assert.IsNotNull(result.Analysis);
        var analysis = result.Analysis!;
        var type = analysis.Types.First(t => t.Name == "ExplicitContractService");
        var method = type.Methods!.FirstOrDefault(m => m.Name == "IAuditable.Audit");
        Assert.IsNotNull(method,
            $"Expected explicit implementation 'IAuditable.Audit'. Got: [{string.Join(", ", type.Methods!.Select(m => m.Name))}]");
        Assert.AreEqual("public", method.Access);
    }

    [TestMethod]
    public async Task Analyze_ExplicitInterfaceProperty_QualifiedNameAndPublicAccess()
    {
        var result = await _analyzer.AnalyzeAsync(FixturePath("ExplicitContractService.cs"), CancellationToken.None);

        Assert.IsNotNull(result.Analysis);
        var analysis = result.Analysis!;
        var type = analysis.Types.First(t => t.Name == "ExplicitContractService");
        var prop = type.Properties!.FirstOrDefault(p => p.Name == "IAuditable.AuditLabel");
        Assert.IsNotNull(prop,
            $"Expected explicit implementation 'IAuditable.AuditLabel'. Got: [{string.Join(", ", type.Properties!.Select(p => p.Name))}]");
        Assert.AreEqual("public", prop.Access);
        Assert.AreEqual("get;", prop.Accessors);
    }

    // ── Base-vs-interface heuristic ───────────────────────────────────────────

    [TestMethod]
    public async Task Analyze_IPlusLowercaseBaseType_ReportedAsBaseNotImplements()
    {
        var result = await _analyzer.AnalyzeAsync(FixturePath("IndexManagerCache.cs"), CancellationToken.None);

        Assert.IsNotNull(result.Analysis);
        var analysis = result.Analysis!;
        var type = analysis.Types.First(t => t.Name == "IndexManagerCache");
        Assert.AreEqual("IndexManager", type.Base);
        Assert.IsNull(type.Implements);
    }

    [TestMethod]
    public async Task Analyze_IPlusUppercaseFirstEntry_StillImplements()
    {
        var result = await _analyzer.AnalyzeAsync(FixturePath("ExplicitContractService.cs"), CancellationToken.None);

        Assert.IsNotNull(result.Analysis);
        var analysis = result.Analysis!;
        var type = analysis.Types.First(t => t.Name == "ExplicitContractService");
        Assert.IsNull(type.Base);
        CollectionAssert.AreEqual(new[] { "IAuditable" }, type.Implements!.ToArray());
    }

    // ── Public events ─────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Analyze_PublicEvents_FieldAndAccessorFormsInDeclarationOrder()
    {
        var result = await _analyzer.AnalyzeAsync(FixturePath("EventPublisher.cs"), CancellationToken.None);

        Assert.IsNotNull(result.Analysis);
        var analysis = result.Analysis!;
        var type = analysis.Types.First(t => t.Name == "EventPublisher");
        Assert.IsNotNull(type.Events);
        CollectionAssert.AreEqual(
            new[] { "Started", "Progressed", "Completed", "Custom" },
            type.Events!.Select(e => e.Name).ToArray());
        Assert.AreEqual("EventHandler?", type.Events[0].Type);
        Assert.AreEqual("EventHandler<string>?", type.Events[1].Type);
        Assert.AreEqual("EventHandler<string>?", type.Events[2].Type);
        Assert.AreEqual("EventHandler?", type.Events[3].Type);
        Assert.IsTrue(type.Events.All(e => e.Access == "public"));
        Assert.IsTrue(type.Events.All(e => e.Accessors is null));
    }

    [TestMethod]
    public async Task Analyze_PrivateEvent_Excluded()
    {
        var result = await _analyzer.AnalyzeAsync(FixturePath("EventPublisher.cs"), CancellationToken.None);

        Assert.IsNotNull(result.Analysis);
        var analysis = result.Analysis!;
        var type = analysis.Types.First(t => t.Name == "EventPublisher");
        Assert.IsFalse(type.Events!.Any(e => e.Name == "InternalOnly"));
    }

    [TestMethod]
    public async Task Analyze_TypeWithoutEvents_EventsIsNull()
    {
        var result = await _analyzer.AnalyzeAsync(FixturePath("ServiceWithDependencies.cs"), CancellationToken.None);

        Assert.IsNotNull(result.Analysis);
        var analysis = result.Analysis!;
        var type = analysis.Types.First(t => t.Name == "ServiceWithDependencies");
        Assert.IsNull(type.Events);
    }

    // ── File metadata ─────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Analyze_ReturnsCorrectFileName()
    {
        var result = await _analyzer.AnalyzeAsync(FixturePath("OrderStatus.cs"), CancellationToken.None);

        Assert.IsNotNull(result.Analysis);
        Assert.AreEqual("OrderStatus.cs", result.Analysis!.File);
    }

    // ── Determinism ───────────────────────────────────────────────────────────

    [TestMethod]
    public async Task FileAnalyzer_IsDeterministic()
    {
        var path = FixturePath("ServiceWithDependencies.cs");

        var first = await _analyzer.AnalyzeAsync(path, CancellationToken.None);
        var second = await _analyzer.AnalyzeAsync(path, CancellationToken.None);

        var json1 = JsonSerializer.Serialize(first.Analysis, AnalysisJson.Options);
        var json2 = JsonSerializer.Serialize(second.Analysis, AnalysisJson.Options);

        Assert.AreEqual(json1, json2);
    }
}

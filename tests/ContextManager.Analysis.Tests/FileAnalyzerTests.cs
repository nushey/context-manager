using System.Text.Json;
using ContextManager.Analysis;
using ContextManager.Analysis.Models;
using ContextManager.Mcp.Serialization;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ContextManager.Analysis.Tests;

[TestClass]
public class FileAnalyzerTests
{
    private static string FixturePath(string name)
        => Path.Combine(
            Path.GetDirectoryName(typeof(FileAnalyzerTests).Assembly.Location)!,
            "Fixtures",
            name);

    // ── Error paths ──────────────────────────────────────────────────────────

    [TestMethod]
    public void Analyze_NonExistentPath_ReturnsFileNotFound()
    {
        var result = FileAnalyzer.Analyze("/does/not/exist/foo.cs", CancellationToken.None);

        Assert.IsInstanceOfType(result, typeof(AnalysisError));
        var error = (AnalysisError)result;
        Assert.AreEqual("file_not_found", error.Code);
        Assert.AreEqual("/does/not/exist/foo.cs", error.FilePath);
    }

    [TestMethod]
    public void Analyze_NonCsExtension_ReturnsNotACsFile()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            var txtPath = Path.ChangeExtension(tmp, ".txt");
            File.Move(tmp, txtPath);
            var result = FileAnalyzer.Analyze(txtPath, CancellationToken.None);

            Assert.IsInstanceOfType(result, typeof(AnalysisError));
            var error = (AnalysisError)result;
            Assert.AreEqual("not_a_cs_file", error.Code);
            Assert.AreEqual(txtPath, error.FilePath);
        }
        finally
        {
            var txtPath = Path.ChangeExtension(tmp, ".txt");
            if (File.Exists(txtPath)) File.Delete(txtPath);
        }
    }

    [TestMethod]
    public void Analyze_UnparseableCSharp_ReturnsParseFailedWithFirstError()
    {
        var tmp = Path.ChangeExtension(Path.GetTempFileName(), ".cs");
        try
        {
            File.WriteAllText(tmp, "class Broken { public void Foo( { }");
            var result = FileAnalyzer.Analyze(tmp, CancellationToken.None);

            Assert.IsInstanceOfType(result, typeof(AnalysisError));
            var error = (AnalysisError)result;
            Assert.AreEqual("parse_failed", error.Code);
            Assert.IsNotNull(error.Message);
            Assert.IsTrue(error.Message.Length > 0);
            Assert.AreEqual(tmp, error.FilePath);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    // ── Namespace: file-scoped ───────────────────────────────────────────────

    [TestMethod]
    public void Analyze_FileScopedNamespace_PopulatesNamespace()
    {
        var result = FileAnalyzer.Analyze(FixturePath("OrderStatus.cs"), CancellationToken.None);

        Assert.IsInstanceOfType(result, typeof(FileAnalysis));
        var analysis = (FileAnalysis)result;
        Assert.AreEqual("ContextManager.Analysis.Tests.Fixtures", analysis.Namespace);
    }

    // ── Namespace: classic block ──────────────────────────────────────────────

    [TestMethod]
    public void Analyze_ClassicBlockNamespace_PopulatesNamespace()
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
            var result = FileAnalyzer.Analyze(tmp, CancellationToken.None);

            Assert.IsInstanceOfType(result, typeof(FileAnalysis));
            var analysis = (FileAnalysis)result;
            Assert.AreEqual("My.Block.Namespace", analysis.Namespace);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    // ── Usings ───────────────────────────────────────────────────────────────

    [TestMethod]
    public void Analyze_ServiceWithDependencies_UsingsInDeclarationOrder()
    {
        var result = FileAnalyzer.Analyze(FixturePath("ServiceWithDependencies.cs"), CancellationToken.None);

        Assert.IsInstanceOfType(result, typeof(FileAnalysis));
        var analysis = (FileAnalysis)result;
        Assert.AreEqual(1, analysis.Usings.Count);
        Assert.AreEqual("System.Threading", analysis.Usings[0]);
    }

    // ── ServiceWithDependencies ───────────────────────────────────────────────

    [TestMethod]
    public void Analyze_ServiceWithDependencies_ConstructorDependencies()
    {
        var result = FileAnalyzer.Analyze(FixturePath("ServiceWithDependencies.cs"), CancellationToken.None);

        Assert.IsInstanceOfType(result, typeof(FileAnalysis));
        var analysis = (FileAnalysis)result;
        var type = analysis.Types.First(t => t.Name == "ServiceWithDependencies");
        Assert.AreEqual(2, type.ConstructorDependencies.Count);
        Assert.AreEqual("IOrderRepository", type.ConstructorDependencies[0].Type);
        Assert.AreEqual("orderRepository", type.ConstructorDependencies[0].Name);
        Assert.AreEqual("IEventBus", type.ConstructorDependencies[1].Type);
        Assert.AreEqual("eventBus", type.ConstructorDependencies[1].Name);
    }

    [TestMethod]
    public void Analyze_ServiceWithDependencies_MethodAttributes()
    {
        var result = FileAnalyzer.Analyze(FixturePath("ServiceWithDependencies.cs"), CancellationToken.None);

        Assert.IsInstanceOfType(result, typeof(FileAnalysis));
        var analysis = (FileAnalysis)result;
        var type = analysis.Types.First(t => t.Name == "ServiceWithDependencies");
        var processOrder = type.Methods.First(m => m.Name == "ProcessOrder");
        Assert.IsTrue(processOrder.Attributes.Any(a => a.Contains("Authorize")));
    }

    [TestMethod]
    public void Analyze_ServiceWithDependencies_PrivateMethodExcluded()
    {
        var result = FileAnalyzer.Analyze(FixturePath("ServiceWithDependencies.cs"), CancellationToken.None);

        Assert.IsInstanceOfType(result, typeof(FileAnalysis));
        var analysis = (FileAnalysis)result;
        var type = analysis.Types.First(t => t.Name == "ServiceWithDependencies");
        Assert.IsFalse(type.Methods.Any(m => m.Name == "InternalHelper"));
    }

    // ── OrderServiceInterface ─────────────────────────────────────────────────

    [TestMethod]
    public void Analyze_OrderServiceInterface_KindIsInterface()
    {
        var result = FileAnalyzer.Analyze(FixturePath("OrderServiceInterface.cs"), CancellationToken.None);

        Assert.IsInstanceOfType(result, typeof(FileAnalysis));
        var analysis = (FileAnalysis)result;
        var type = analysis.Types.First(t => t.Name == "IOrderService");
        Assert.AreEqual("interface", type.Kind);
    }

    [TestMethod]
    public void Analyze_OrderServiceInterface_MethodList()
    {
        var result = FileAnalyzer.Analyze(FixturePath("OrderServiceInterface.cs"), CancellationToken.None);

        Assert.IsInstanceOfType(result, typeof(FileAnalysis));
        var analysis = (FileAnalysis)result;
        var type = analysis.Types.First(t => t.Name == "IOrderService");
        Assert.AreEqual(2, type.Methods.Count);
        Assert.AreEqual("Process", type.Methods[0].Name);
        Assert.AreEqual("Cancel", type.Methods[1].Name);
    }

    // ── CreateOrderRecord ─────────────────────────────────────────────────────

    [TestMethod]
    public void Analyze_CreateOrderRecord_KindIsRecord()
    {
        var result = FileAnalyzer.Analyze(FixturePath("CreateOrderRecord.cs"), CancellationToken.None);

        Assert.IsInstanceOfType(result, typeof(FileAnalysis));
        var analysis = (FileAnalysis)result;
        var type = analysis.Types.First(t => t.Name == "CreateOrderRecord");
        Assert.AreEqual("record", type.Kind);
    }

    [TestMethod]
    public void Analyze_CreateOrderRecord_PrimaryConstructorInConstructorDependencies()
    {
        var result = FileAnalyzer.Analyze(FixturePath("CreateOrderRecord.cs"), CancellationToken.None);

        Assert.IsInstanceOfType(result, typeof(FileAnalysis));
        var analysis = (FileAnalysis)result;
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
    public void Analyze_CreateOrderRecord_PrimaryConstructorNotInMethods()
    {
        var result = FileAnalyzer.Analyze(FixturePath("CreateOrderRecord.cs"), CancellationToken.None);

        Assert.IsInstanceOfType(result, typeof(FileAnalysis));
        var analysis = (FileAnalysis)result;
        var type = analysis.Types.First(t => t.Name == "CreateOrderRecord");
        Assert.IsFalse(type.Methods.Any(m => m.Name == "CreateOrderRecord"));
    }

    // ── Money (struct) ────────────────────────────────────────────────────────

    [TestMethod]
    public void Analyze_Money_KindIsStruct()
    {
        var result = FileAnalyzer.Analyze(FixturePath("Money.cs"), CancellationToken.None);

        Assert.IsInstanceOfType(result, typeof(FileAnalysis));
        var analysis = (FileAnalysis)result;
        var type = analysis.Types.First(t => t.Name == "Money");
        Assert.AreEqual("struct", type.Kind);
    }

    [TestMethod]
    public void Analyze_Money_PropertiesPopulated()
    {
        var result = FileAnalyzer.Analyze(FixturePath("Money.cs"), CancellationToken.None);

        Assert.IsInstanceOfType(result, typeof(FileAnalysis));
        var analysis = (FileAnalysis)result;
        var type = analysis.Types.First(t => t.Name == "Money");
        Assert.AreEqual(2, type.Properties.Count);
        Assert.AreEqual("Amount", type.Properties[0].Name);
        Assert.AreEqual("Currency", type.Properties[1].Name);
    }

    // ── OrderStatus (enum) ────────────────────────────────────────────────────

    [TestMethod]
    public void Analyze_OrderStatus_KindIsEnum()
    {
        var result = FileAnalyzer.Analyze(FixturePath("OrderStatus.cs"), CancellationToken.None);

        Assert.IsInstanceOfType(result, typeof(FileAnalysis));
        var analysis = (FileAnalysis)result;
        var type = analysis.Types.First(t => t.Name == "OrderStatus");
        Assert.AreEqual("enum", type.Kind);
    }

    [TestMethod]
    public void Analyze_OrderStatus_MembersNamesOnly()
    {
        var result = FileAnalyzer.Analyze(FixturePath("OrderStatus.cs"), CancellationToken.None);

        Assert.IsInstanceOfType(result, typeof(FileAnalysis));
        var analysis = (FileAnalysis)result;
        var type = analysis.Types.First(t => t.Name == "OrderStatus");
        Assert.IsNotNull(type.Members);
        CollectionAssert.AreEqual(
            new[] { "Pending", "Processing", "Completed" },
            type.Members!.ToArray());
    }

    // ── DTO branch (a): no methods ────────────────────────────────────────────

    [TestMethod]
    public void Analyze_DtoByNoMethods_KindIsDto()
    {
        var result = FileAnalyzer.Analyze(FixturePath("DtoByNoMethods.cs"), CancellationToken.None);

        Assert.IsInstanceOfType(result, typeof(FileAnalysis));
        var analysis = (FileAnalysis)result;
        var type = analysis.Types.First(t => t.Name == "OrderSummary");
        Assert.AreEqual("dto", type.Kind);
    }

    [TestMethod]
    public void Analyze_DtoByNoMethods_PropertiesEmpty()
    {
        var result = FileAnalyzer.Analyze(FixturePath("DtoByNoMethods.cs"), CancellationToken.None);

        Assert.IsInstanceOfType(result, typeof(FileAnalysis));
        var analysis = (FileAnalysis)result;
        var type = analysis.Types.First(t => t.Name == "OrderSummary");
        Assert.AreEqual(0, type.Properties.Count);
    }

    // ── DTO branch (b): parameterless ctor + auto-properties ─────────────────

    [TestMethod]
    public void Analyze_DtoByAutoProperties_KindIsDto()
    {
        var result = FileAnalyzer.Analyze(FixturePath("DtoByAutoProperties.cs"), CancellationToken.None);

        Assert.IsInstanceOfType(result, typeof(FileAnalysis));
        var analysis = (FileAnalysis)result;
        var type = analysis.Types.First(t => t.Name == "CustomerInfo");
        Assert.AreEqual("dto", type.Kind);
    }

    [TestMethod]
    public void Analyze_DtoByAutoProperties_PropertiesEmpty()
    {
        var result = FileAnalyzer.Analyze(FixturePath("DtoByAutoProperties.cs"), CancellationToken.None);

        Assert.IsInstanceOfType(result, typeof(FileAnalysis));
        var analysis = (FileAnalysis)result;
        var type = analysis.Types.First(t => t.Name == "CustomerInfo");
        Assert.AreEqual(0, type.Properties.Count);
    }

    // ── DTO branch (c): suffix-based ──────────────────────────────────────────

    [TestMethod]
    public void Analyze_CreateOrderRequest_KindIsDto()
    {
        var result = FileAnalyzer.Analyze(FixturePath("CreateOrderRequest.cs"), CancellationToken.None);

        Assert.IsInstanceOfType(result, typeof(FileAnalysis));
        var analysis = (FileAnalysis)result;
        var type = analysis.Types.First(t => t.Name == "CreateOrderRequest");
        Assert.AreEqual("dto", type.Kind);
    }

    [TestMethod]
    public void Analyze_CreateOrderRequest_PropertiesEmpty()
    {
        var result = FileAnalyzer.Analyze(FixturePath("CreateOrderRequest.cs"), CancellationToken.None);

        Assert.IsInstanceOfType(result, typeof(FileAnalysis));
        var analysis = (FileAnalysis)result;
        var type = analysis.Types.First(t => t.Name == "CreateOrderRequest");
        Assert.AreEqual(0, type.Properties.Count);
    }

    // ── File metadata ─────────────────────────────────────────────────────────

    [TestMethod]
    public void Analyze_ReturnsCorrectFileName()
    {
        var result = FileAnalyzer.Analyze(FixturePath("OrderStatus.cs"), CancellationToken.None);

        Assert.IsInstanceOfType(result, typeof(FileAnalysis));
        var analysis = (FileAnalysis)result;
        Assert.AreEqual("OrderStatus.cs", analysis.File);
    }

    // ── Determinism ───────────────────────────────────────────────────────────

    [TestMethod]
    public void FileAnalyzer_IsDeterministic()
    {
        var path = FixturePath("ServiceWithDependencies.cs");

        var first = FileAnalyzer.Analyze(path, CancellationToken.None);
        var second = FileAnalyzer.Analyze(path, CancellationToken.None);

        var json1 = JsonSerializer.Serialize(first, AnalysisJson.Options);
        var json2 = JsonSerializer.Serialize(second, AnalysisJson.Options);

        Assert.AreEqual(json1, json2);
    }
}

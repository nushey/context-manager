using System.Text.Json;
using ContextManager.Analysis;
using ContextManager.Analysis.Extraction;
using ContextManager.Analysis.Models;
using ContextManager.Mcp.Serialization;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ContextManager.Analysis.Tests;

[TestClass]
public class ContextAnalyzerTests
{
    private static string FixturePath(string name)
        => Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "Fixtures",
            "ContextFixtures",
            name);

    private static ContextAnalyzer CreateAnalyzer() => new(new CrossReferenceResolver());

    private static readonly string IOrderServicePath = FixturePath("IOrderService.cs");
    private static readonly string IOrderRepositoryPath = FixturePath("IOrderRepository.cs");
    private static readonly string OrderServicePath = FixturePath("OrderService.cs");
    private static readonly string OrderControllerPath = FixturePath("OrderController.cs");

    private static IReadOnlyList<string> ThreeFilePaths =>
        [IOrderServicePath, IOrderRepositoryPath, OrderServicePath];

    [TestMethod]
    public async Task AnalyzeAsync_WithAllFourFixtures_ReturnsThreeFiles()
    {
        var analyzer = CreateAnalyzer();
        var result = await analyzer.AnalyzeAsync(ThreeFilePaths);

        Assert.AreEqual(3, result.Files.Count);
    }

    [TestMethod]
    public async Task AnalyzeAsync_OrderServiceImplementsIOrderService_InReferences()
    {
        var analyzer = CreateAnalyzer();
        var result = await analyzer.AnalyzeAsync(ThreeFilePaths);

        var reference = result.References.FirstOrDefault(r =>
            r.From == "OrderService" &&
            r.To == "IOrderService" &&
            r.Via == "implements");

        Assert.IsNotNull(reference, "Expected reference: OrderService implements IOrderService");
        Assert.IsNotNull(reference.ResolvedFile, "ResolvedFile should be set for IOrderService (it's in the set)");
        Assert.IsTrue(
            string.Equals(reference.ResolvedFile, IOrderServicePath, StringComparison.OrdinalIgnoreCase),
            $"ResolvedFile should be the IOrderService fixture path, got: {reference.ResolvedFile}");
    }

    [TestMethod]
    public async Task AnalyzeAsync_OrderServiceDependsOnIOrderRepository_InReferences()
    {
        var analyzer = CreateAnalyzer();
        var result = await analyzer.AnalyzeAsync(ThreeFilePaths);

        var reference = result.References.FirstOrDefault(r =>
            r.From == "OrderService" &&
            r.To == "IOrderRepository" &&
            r.Via == "constructor");

        Assert.IsNotNull(reference, "Expected reference: OrderService depends on IOrderRepository via constructor");
        Assert.IsNotNull(reference.ResolvedFile, "ResolvedFile should be set for IOrderRepository (it's in the set)");
        Assert.IsTrue(
            string.Equals(reference.ResolvedFile, IOrderRepositoryPath, StringComparison.OrdinalIgnoreCase),
            $"ResolvedFile should be the IOrderRepository fixture path, got: {reference.ResolvedFile}");
    }

    [TestMethod]
    public async Task AnalyzeAsync_OrderController_CreateOrderDtoIsUnresolved()
    {
        var paths = new[] { IOrderServicePath, IOrderRepositoryPath, OrderServicePath, OrderControllerPath };
        var analyzer = CreateAnalyzer();
        var result = await analyzer.AnalyzeAsync(paths);

        Assert.IsTrue(
            result.Unresolved.Contains("CreateOrderDto", StringComparer.Ordinal),
            $"Expected 'CreateOrderDto' in unresolved. Got: [{string.Join(", ", result.Unresolved)}]");
    }

    [TestMethod]
    public async Task AnalyzeAsync_MethodsAreStrings_NotObjects()
    {
        var analyzer = CreateAnalyzer();
        var result = await analyzer.AnalyzeAsync(ThreeFilePaths);

        var orderServiceFile = result.Files.First(f =>
            Path.GetFileName(f.File).Equals("OrderService.cs", StringComparison.OrdinalIgnoreCase));

        var typeNames = string.Join(", ", orderServiceFile.Types.Select(t => t.Name));
        var orderServiceType = orderServiceFile.Types.FirstOrDefault(t => t.Name == "OrderService");
        Assert.IsNotNull(orderServiceType,
            $"Expected type 'OrderService' in types. Found: [{typeNames}]");

        Assert.IsNotNull(orderServiceType.Methods, "OrderService should have methods");
        Assert.IsTrue(orderServiceType.Methods!.Count > 0, "Methods collection should not be empty");

        var firstMethod = orderServiceType.Methods[0];
        Assert.IsInstanceOfType(firstMethod, typeof(string), "Each method entry must be a string");
        Assert.IsTrue(firstMethod.Contains("(") && firstMethod.Contains(")") && firstMethod.Contains(":"),
            $"Method string should match 'Name(params): ReturnType' format. Got: {firstMethod}");
    }

    [TestMethod]
    public async Task AnalyzeAsync_PropertiesNeverInOutput()
    {
        var analyzer = CreateAnalyzer();
        var result = await analyzer.AnalyzeAsync(ThreeFilePaths);

        foreach (var file in result.Files)
        {
            foreach (var type in file.Types)
            {
                // ContextTypeInfo has no Properties field at all — this is a compile-time guarantee.
                // Verify via JSON serialization that no "properties" key appears in any type object.
                var json = JsonSerializer.Serialize(type, AnalysisJson.Options);
                Assert.IsFalse(json.Contains("\"properties\""),
                    $"Type '{type.Name}' in '{file.File}' has a 'properties' key in JSON output");
            }
        }
    }

    [TestMethod]
    public async Task AnalyzeAsync_DeterministicOutput_SameInputSameJson()
    {
        var analyzer = CreateAnalyzer();

        var result1 = await analyzer.AnalyzeAsync(ThreeFilePaths);
        var result2 = await analyzer.AnalyzeAsync(ThreeFilePaths);

        var json1 = JsonSerializer.Serialize(result1, AnalysisJson.Options);
        var json2 = JsonSerializer.Serialize(result2, AnalysisJson.Options);

        Assert.AreEqual(json1, json2, "Serialized ContextAnalysis must be identical across two calls with the same input");
    }
}

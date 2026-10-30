using ContextManager.Analysis;
using ContextManager.Mcp.Tools;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ContextManager.Analysis.Tests.Tools;

[TestClass]
public class InspectFileToolTests
{
    private static string FixturePath(string name)
        => Path.Combine(
            Path.GetDirectoryName(typeof(InspectFileToolTests).Assembly.Location)!,
            "Fixtures",
            name);

    private static readonly InspectFileTool _tool = new(new FileAnalyzer());

    [TestMethod]
    public async Task AnalyzeAsync_Compact_OmitsVerboseFieldsAndKeepsMethodOneLiners()
    {
        var json = await _tool.AnalyzeAsync(FixturePath("ServiceWithDependencies.cs"), compact: true, CancellationToken.None);

        // Methods survive as one-liner signatures (method name is present).
        Assert.IsTrue(json.Contains("ProcessOrder"), $"Compact output should carry the method one-liner. Got: {json}");
        // Verbose per-method detail is dropped.
        Assert.IsFalse(json.Contains("startLine"), $"Compact output must omit startLine. Got: {json}");
        // Properties are dropped in compact mode.
        Assert.IsFalse(json.Contains("OrderCount"), $"Compact output must omit properties. Got: {json}");
    }

    [TestMethod]
    public async Task AnalyzeAsync_Full_KeepsVerboseFields()
    {
        var json = await _tool.AnalyzeAsync(FixturePath("ServiceWithDependencies.cs"), compact: false, CancellationToken.None);

        Assert.IsTrue(json.Contains("startLine"), $"Full output should contain startLine. Got: {json}");
        Assert.IsTrue(json.Contains("OrderCount"), $"Full output should contain properties. Got: {json}");
    }
}

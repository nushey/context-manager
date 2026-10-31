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
    public async Task AnalyzeAsync_ReturnsFullContract()
    {
        var json = await _tool.AnalyzeAsync(FixturePath("ServiceWithDependencies.cs"), CancellationToken.None);

        Assert.IsTrue(json.Contains("startLine"), $"Full output should contain startLine. Got: {json}");
        Assert.IsTrue(json.Contains("OrderCount"), $"Full output should contain properties. Got: {json}");
    }
}

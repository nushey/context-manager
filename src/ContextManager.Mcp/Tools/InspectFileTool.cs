using System.ComponentModel;
using System.Text.Json;
using ContextManager.Analysis;
using ModelContextProtocol.Server;

namespace ContextManager.Mcp.Tools;

[McpServerToolType]
public sealed class InspectFileTool
{
    private readonly FileAnalyzer _analyzer;

    public InspectFileTool(FileAnalyzer analyzer)
    {
        _analyzer = analyzer;
    }

    [McpServerTool(Name = "inspect_file")]
    [Description("Returns a structural JSON contract for a single C# file.")]
    public async Task<string> AnalyzeAsync(
        [Description("Absolute or working-directory-relative path to a .cs file.")] string filePath,
        CancellationToken cancellationToken)
    {
        var result = await _analyzer.AnalyzeAsync(filePath, cancellationToken);
        var payload = result.Analysis is not null ? (object)result.Analysis : result.Error!;
        return JsonSerializer.Serialize(payload, AnalysisJson.Options);
    }
}

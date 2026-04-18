using System.ComponentModel;
using System.Text.Json;
using ContextManager.Analysis;
using ContextManager.Mcp.Serialization;
using ModelContextProtocol.Server;

namespace ContextManager.Mcp.Tools;

[McpServerToolType]
public sealed class InspectFileTool
{
    [McpServerTool(Name = "inspect_file")]
    [Description("Returns a structural JSON contract for a single C# file.")]
    public string Analyze(
        [Description("Absolute or working-directory-relative path to a .cs file.")] string filePath,
        CancellationToken cancellationToken)
    {
        var result = FileAnalyzer.Analyze(filePath, cancellationToken);
        return JsonSerializer.Serialize(result, AnalysisJson.Options);
    }
}

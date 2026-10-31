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
    [Description("Returns a structural JSON contract for a single C# file: its absolute path, namespace, usings, and each non-private type with its kind, base type, implemented interfaces, attributes, constructor dependencies, methods (with line numbers), properties, and events.")]
    public async Task<string> AnalyzeAsync(
        [Description("Absolute or working-directory-relative path to a .cs file.")] string filePath,
        CancellationToken cancellationToken = default)
    {
        var result = await _analyzer.AnalyzeAsync(filePath, cancellationToken);
        return result.Analysis is null
            ? JsonSerializer.Serialize(result.Error, AnalysisJson.Options)
            : JsonSerializer.Serialize(result.Analysis, AnalysisJson.Options);
    }
}

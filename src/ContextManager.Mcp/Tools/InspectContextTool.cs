using System.ComponentModel;
using System.Text.Json;
using ContextManager.Analysis;
using ContextManager.Analysis.Models;
using ContextManager.Mcp.Serialization;
using ModelContextProtocol.Server;

namespace ContextManager.Mcp.Tools;

[McpServerToolType]
public sealed class InspectContextTool
{
    private readonly ContextAnalyzer _analyzer;

    public InspectContextTool(ContextAnalyzer analyzer)
    {
        _analyzer = analyzer;
    }

    [McpServerTool(Name = "inspect_context"), Description("Analyze cross-file relationships in up to 15 C# source files using Roslyn semantic model. BCL/framework types (e.g. CancellationToken, Task) are resolved via framework metadata and EXCLUDED from 'unresolved' — that list only contains user types missing from the input set. Output is a compressed format by design: methods are one-line strings ('Name(params): ReturnType'), no properties, no line numbers. There is no parity with inspect_file — use inspect_file for full per-type detail.")]
    public async Task<string> InspectContextAsync(
        [Description("List of absolute paths to .cs files to analyze (max 15).")] IReadOnlyList<string> filePaths,
        CancellationToken ct = default)
    {
        if (filePaths.Count > 15)
            return JsonSerializer.Serialize(
                new AnalysisError("too_many_files", $"Expected at most 15 files, got {filePaths.Count}.", null),
                AnalysisJson.Options);

        foreach (var path in filePaths)
        {
            if (!File.Exists(path))
                return JsonSerializer.Serialize(
                    new AnalysisError("file_not_found", $"File not found: {path}", path),
                    AnalysisJson.Options);
        }

        var result = await _analyzer.AnalyzeAsync(filePaths, ct);
        return JsonSerializer.Serialize(result, AnalysisJson.Options);
    }
}

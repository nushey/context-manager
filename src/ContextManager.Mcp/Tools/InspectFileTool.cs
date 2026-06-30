using System.ComponentModel;
using System.Text.Json;
using ContextManager.Analysis;
using ContextManager.Analysis.Extraction;
using ContextManager.Analysis.Models;
using ContextManager.Mcp.Serialization;
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
    [Description("Returns a structural JSON contract for a single C# file. Set compact=true for a token-saving view that keeps each type's name/kind/base/implements and emits methods as one-liner 'Name(params): ReturnType' strings while omitting properties, events, modifiers, line numbers, attributes, and constructor dependencies.")]
    public async Task<string> AnalyzeAsync(
        [Description("Absolute or working-directory-relative path to a .cs file.")] string filePath,
        [Description("When true, return a compact contract: methods become one-liner signatures and verbose fields (properties, events, line numbers, modifiers, attributes, constructor dependencies) are omitted. Default false returns the full structural contract.")] bool compact = false,
        CancellationToken cancellationToken = default)
    {
        var result = await _analyzer.AnalyzeAsync(filePath, cancellationToken);

        var analysis = result.Analysis;
        if (analysis is null)
            return JsonSerializer.Serialize(result.Error, AnalysisJson.Options);

        object payload = compact ? ToCompact(analysis) : analysis;
        return JsonSerializer.Serialize(payload, AnalysisJson.Options);
    }

    private static CompactFileAnalysis ToCompact(FileAnalysis analysis) => new(
        File: analysis.File,
        Namespace: analysis.Namespace,
        Types: analysis.Types.Select(ToCompactType).ToList(),
        ParseErrors: analysis.ParseErrors);

    private static CompactTypeInfo ToCompactType(TypeInfo type) => new(
        Name: type.Name,
        Kind: type.Kind,
        Base: type.Base,
        Implements: type.Implements,
        Methods: type.Methods is null ? null : type.Methods.Select(MethodSignatureFormatter.Format).ToList());
}

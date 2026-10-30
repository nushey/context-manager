using System.ComponentModel;
using System.Text.Json;
using ContextManager.Analysis;
using ContextManager.Analysis.Extraction;
using ContextManager.Analysis.Graph;
using ContextManager.Analysis.Models;
using ContextManager.Mcp.Serialization;
using ModelContextProtocol.Server;

namespace ContextManager.Mcp.Tools;

[McpServerToolType]
public sealed class InspectFileTool
{
    private readonly FileAnalyzer _analyzer;
    private readonly IGraphEnricher? _enricher;

    public InspectFileTool(FileAnalyzer analyzer, IGraphEnricher? enricher = null)
    {
        _analyzer = analyzer;
        _enricher = enricher;
    }

    [McpServerTool(Name = "inspect_file")]
    [Description("Returns a structural JSON contract for a single C# file. Set compact=true for a token-saving view that keeps each type's name/kind/base/implements and emits methods as one-liner 'Name(params): ReturnType' strings while omitting properties, events, modifiers, line numbers, attributes, and constructor dependencies. When the file's enclosing solution has a persisted knowledge graph, each type found in it carries 'graph' (nodeId, inbound/outbound neighbor counts, and up to 5 directCallers/directDependencies with their id and file); the graph is read as last persisted and never synchronized here. When the enclosing directory has exactly one .sln and no graph yet, 'graphStatus' is not_initialized with the .sln to scan.")]
    public async Task<string> AnalyzeAsync(
        [Description("Absolute or working-directory-relative path to a .cs file.")] string filePath,
        [Description("When true, return a compact contract: methods become one-liner signatures and verbose fields (properties, events, line numbers, modifiers, attributes, constructor dependencies) are omitted. Default false returns the full structural contract.")] bool compact = false,
        CancellationToken cancellationToken = default)
    {
        var result = await _analyzer.AnalyzeAsync(filePath, cancellationToken);

        var analysis = result.Analysis;
        if (analysis is null)
            return JsonSerializer.Serialize(result.Error, AnalysisJson.Options);

        if (_enricher is not null)
            analysis = _enricher.Enrich(analysis, filePath);

        object payload = compact ? ToCompact(analysis) : analysis;
        return JsonSerializer.Serialize(payload, AnalysisJson.Options);
    }

    private static CompactFileAnalysis ToCompact(FileAnalysis analysis) => new(
        File: analysis.File,
        Namespace: analysis.Namespace,
        Types: analysis.Types.Select(ToCompactType).ToList(),
        ParseErrors: analysis.ParseErrors,
        GraphStatus: analysis.GraphStatus);

    private static CompactTypeInfo ToCompactType(TypeInfo type) => new(
        Name: type.Name,
        Kind: type.Kind,
        Base: type.Base,
        Implements: type.Implements,
        Methods: type.Methods is null ? null : type.Methods.Select(MethodSignatureFormatter.Format).ToList(),
        Graph: type.Graph);
}

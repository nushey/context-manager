using ContextManager.Analysis;
using ContextManager.Analysis.Extraction;
using ContextManager.Analysis.Graph;
using ContextManager.Mcp.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace ContextManager.Mcp;

public static class ContextManagerServices
{
    public static IServiceCollection AddContextManagerServices(this IServiceCollection services) => services
        .AddSingleton<FileAnalyzer>()
        .AddSingleton<CrossReferenceResolver>()
        .AddSingleton<ContextAnalyzer>()
        .AddSingleton<GraphRegistry>()
        .AddSingleton<EdgeExtractor>()
        .AddSingleton<GraphBuilder>()
        .AddSingleton<IGraphSynchronizer>(provider => new GraphSynchronizer(
            provider.GetRequiredService<GraphBuilder>(),
            provider.GetRequiredService<GraphRegistry>(),
            MsBuildBootstrap.EnsureRegistered));
}

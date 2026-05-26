using ContextManager.Analysis;
using ContextManager.Analysis.Extraction;
using ContextManager.Analysis.Graph;
using Microsoft.Build.Locator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

MSBuildLocator.RegisterDefaults();
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Services
    .AddSingleton<CrossReferenceResolver>()
    .AddSingleton<ContextAnalyzer>()
    .AddSingleton<GraphStore>()
    .AddSingleton<EdgeExtractor>()
    .AddSingleton<GraphBuilder>()
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();
await builder.Build().RunAsync();

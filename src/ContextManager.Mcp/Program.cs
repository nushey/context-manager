using ContextManager.Analysis;
using ContextManager.Analysis.Extraction;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Services
    .AddSingleton<CrossReferenceResolver>()
    .AddSingleton<ContextAnalyzer>()
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();
await builder.Build().RunAsync();

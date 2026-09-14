using ContextManager.Analysis;
using ContextManager.Analysis.Extraction;
using ContextManager.Analysis.Graph;
using ContextManager.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

if (args.Contains("--diagnose-msbuild", StringComparer.OrdinalIgnoreCase))
{
    try
    {
        MsBuildBootstrap.EnsureRegistered();
        Console.Error.WriteLine(MsBuildBootstrap.DescribeRegistration());
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex);
        Environment.ExitCode = 1;
    }

    return;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
    Console.Error.WriteLine($"Unhandled server exception:{Environment.NewLine}{eventArgs.ExceptionObject}");
TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
    Console.Error.WriteLine($"Unobserved server task exception:{Environment.NewLine}{eventArgs.Exception}");
builder.Services
    .AddSingleton<FileAnalyzer>()
    .AddSingleton<CrossReferenceResolver>()
    .AddSingleton<ContextAnalyzer>()
    .AddSingleton<GraphStore>()
    .AddSingleton<EdgeExtractor>()
    .AddSingleton<GraphBuilder>()
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

var host = builder.Build();

var graphPath = ResolveGraphPath(args);
if (graphPath is not null && File.Exists(graphPath))
{
    try
    {
        var store = host.Services.GetRequiredService<GraphStore>();
        store.Deserialize(await File.ReadAllTextAsync(graphPath));
    }
    catch (Exception ex)
    {
        // stderr is safe under the MCP stdio protocol — stdout is reserved for JSON-RPC frames.
        Console.Error.WriteLine($"Failed to load graph cache from '{graphPath}': {ex.Message}. Continuing with an empty graph.");
    }
}

await host.RunAsync();

static string? ResolveGraphPath(string[] args)
{
    // Check --graph <path> CLI argument first.
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i].Equals("--graph", StringComparison.OrdinalIgnoreCase))
            return args[i + 1];
    }

    // Fall back to environment variable.
    return Environment.GetEnvironmentVariable("CONTEXT_MANAGER_GRAPH_PATH");
}

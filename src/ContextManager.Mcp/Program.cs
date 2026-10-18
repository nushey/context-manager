using ContextManager.Analysis;
using ContextManager.Analysis.Extraction;
using ContextManager.Analysis.Graph;
using ContextManager.Mcp;
using ContextManager.Mcp.Setup;
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

if (args.Length > 0 && args[0].Equals("setup", StringComparison.OrdinalIgnoreCase))
{
    var yes = args.Skip(1).Any(arg => arg is "-y" or "--yes");
    Environment.ExitCode = new SetupCommand(
        SetupEnvironment.FromCurrentProcess(),
        Spectre.Console.AnsiConsole.Console,
        new JsonConfigInserter(),
        new TomlConfigAppender()).Run(yes);

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
    .AddSingleton<GraphRegistry>()
    .AddSingleton<EdgeExtractor>()
    .AddSingleton<GraphBuilder>()
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

var host = builder.Build();

await host.RunAsync();

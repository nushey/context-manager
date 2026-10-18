using Spectre.Console;

namespace ContextManager.Mcp.Setup;

public sealed class SetupCommand(
    SetupEnvironment environment,
    IAnsiConsole console,
    JsonConfigInserter jsonInserter,
    TomlConfigAppender tomlAppender)
{
    public const string NonInteractiveMessage = "non-interactive terminal: re-run with -y to install for all supported clients";

    public int Run(bool yes)
    {
        var clients = SetupCatalog.For(environment);
        if (!yes && !console.Profile.Capabilities.Interactive)
        {
            Console.Error.WriteLine(NonInteractiveMessage);
            return 1;
        }

        var selected = yes
            ? clients
            : console.Prompt(new MultiSelectionPrompt<SetupClient>()
                .Title("Select the clients to configure with [green]context-manager[/]:")
                .UseConverter(DescribeChoice)
                .AddChoices(clients));

        var results = selected.Select(Apply).ToList();
        foreach (var result in results)
            WriteResult(result);

        console.MarkupLine(
            $"configured: {Count(results, SetupOutcome.Configured)}, " +
            $"skipped: {Count(results, SetupOutcome.Skipped)}, " +
            $"failed: {Count(results, SetupOutcome.Failed)}");
        WarnIfToolsDirectoryNotOnPath();

        return results.Any(result => result.Outcome == SetupOutcome.Failed) ? 1 : 0;
    }

    public string DescribeChoice(SetupClient client)
    {
        var label = Markup.Escape(client.DisplayName);
        if (Path.Exists(client.DetectionPath))
            label += " [grey](detected)[/]";
        if (IsConfigured(client))
            label += " [grey](already configured)[/]";
        return label;
    }

    private bool IsConfigured(SetupClient client) => client.Format == ConfigFormat.Toml
        ? tomlAppender.IsConfigured(client)
        : jsonInserter.IsConfigured(client);

    private SetupResult Apply(SetupClient client) => client.Format == ConfigFormat.Toml
        ? tomlAppender.Apply(client)
        : jsonInserter.Apply(client);

    private void WriteResult(SetupResult result)
    {
        var name = Markup.Escape(result.Client.DisplayName);
        var line = result.Outcome switch
        {
            SetupOutcome.Configured => $"[green]configured[/]  {name}  {Markup.Escape(result.Client.ConfigPath)}",
            SetupOutcome.Skipped => $"[grey]skipped[/]     {name}  already configured",
            _ => $"[red]failed[/]      {name}  {Markup.Escape(result.Error ?? string.Empty)}"
        };
        console.MarkupLine(line);
    }

    private void WarnIfToolsDirectoryNotOnPath()
    {
        var toolsDirectory = Path.Combine(environment.Home, ".dotnet", "tools");
        var isWindows = environment.Platform == SetupPlatform.Windows;
        var comparison = isWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var onPath = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator)
            .Any(entry => string.Equals(Path.TrimEndingDirectorySeparator(entry), toolsDirectory, comparison));
        if (onPath)
            return;

        var fix = isWindows
            ? $"add {toolsDirectory} to your user PATH (System Properties > Environment Variables)"
            : "add export PATH=\"$PATH:$HOME/.dotnet/tools\" to your shell profile";
        console.MarkupLine(
            $"[yellow]warning:[/] {Markup.Escape(toolsDirectory)} is not on PATH, so clients cannot launch context-manager. " +
            $"Fix: {Markup.Escape(fix)}, then restart your clients.");
    }

    private static int Count(IEnumerable<SetupResult> results, SetupOutcome outcome) =>
        results.Count(result => result.Outcome == outcome);
}

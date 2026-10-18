namespace ContextManager.Mcp.Setup;

public sealed record SetupEnvironment(SetupPlatform Platform, string Home, string AppData, string? XdgConfigHome)
{
    public static SetupEnvironment FromCurrentProcess() => new(
        OperatingSystem.IsWindows() ? SetupPlatform.Windows : OperatingSystem.IsMacOS() ? SetupPlatform.MacOS : SetupPlatform.Linux,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"));
}

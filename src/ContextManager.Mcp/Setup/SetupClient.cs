namespace ContextManager.Mcp.Setup;

public sealed record SetupClient(
    string Id,
    string DisplayName,
    string ConfigPath,
    string DetectionPath,
    ConfigFormat Format,
    string ServersKey,
    string Entry,
    string? NewFileContent);

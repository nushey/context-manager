namespace ContextManager.Mcp.Setup;

public sealed record SetupResult(SetupClient Client, SetupOutcome Outcome, string? Error = null);

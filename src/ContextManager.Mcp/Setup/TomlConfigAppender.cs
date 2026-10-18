using Tomlyn;
using Tomlyn.Model;

namespace ContextManager.Mcp.Setup;

public sealed class TomlConfigAppender
{
    public bool IsConfigured(SetupClient client)
    {
        if (!File.Exists(client.ConfigPath))
            return false;

        try
        {
            return ContainsServer(File.ReadAllText(client.ConfigPath), client.ServersKey);
        }
        catch (TomlException)
        {
            return false;
        }
    }

    public SetupResult Apply(SetupClient client)
    {
        try
        {
            var original = File.Exists(client.ConfigPath) ? File.ReadAllText(client.ConfigPath) : string.Empty;
            if (ContainsServer(original, client.ServersKey))
                return new SetupResult(client, SetupOutcome.Skipped);

            var addition = Separator(original) + $"[{client.ServersKey}.{SetupCatalog.ServerName}]\n{client.Entry}\n";
            if (!ContainsServer(original + addition, client.ServersKey))
                throw new InvalidOperationException($"the result does not contain [{client.ServersKey}.{SetupCatalog.ServerName}]");

            Directory.CreateDirectory(Path.GetDirectoryName(client.ConfigPath)!);
            File.AppendAllText(client.ConfigPath, addition);
            return new SetupResult(client, SetupOutcome.Configured);
        }
        catch (Exception ex)
        {
            return new SetupResult(client, SetupOutcome.Failed, ex.Message);
        }
    }

    private static bool ContainsServer(string toml, string serversKey)
    {
        var table = TomlSerializer.Deserialize<TomlTable>(toml);
        return table is not null
            && table.TryGetValue(serversKey, out var servers)
            && servers is TomlTable serverTable
            && serverTable.ContainsKey(SetupCatalog.ServerName);
    }

    private static string Separator(string original) => original.Length switch
    {
        0 => string.Empty,
        _ when original.EndsWith('\n') => "\n",
        _ => "\n\n"
    };
}

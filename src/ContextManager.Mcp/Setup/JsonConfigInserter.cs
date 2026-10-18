using System.Text;
using System.Text.Json;

namespace ContextManager.Mcp.Setup;

public sealed class JsonConfigInserter
{
    private static readonly JsonReaderOptions ReaderOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public bool IsConfigured(SetupClient client)
    {
        if (!File.Exists(client.ConfigPath))
            return false;

        try
        {
            return Locate(File.ReadAllBytes(client.ConfigPath), client.ServersKey).IsConfigured;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public SetupResult Apply(SetupClient client)
    {
        try
        {
            byte[] updated;
            if (File.Exists(client.ConfigPath) && new FileInfo(client.ConfigPath).Length > 0)
            {
                var original = File.ReadAllBytes(client.ConfigPath);
                var target = Locate(original, client.ServersKey);
                if (target.IsConfigured)
                    return new SetupResult(client, SetupOutcome.Skipped);

                updated = Insert(original, target, client);
            }
            else
            {
                updated = Encoding.UTF8.GetBytes(client.NewFileContent!);
            }

            Validate(updated, client.ServersKey);
            Directory.CreateDirectory(Path.GetDirectoryName(client.ConfigPath)!);
            File.WriteAllBytes(client.ConfigPath, updated);
            return new SetupResult(client, SetupOutcome.Configured);
        }
        catch (Exception ex)
        {
            return new SetupResult(client, SetupOutcome.Failed, ex.Message);
        }
    }

    private static byte[] Insert(byte[] original, InsertionTarget target, SetupClient client)
    {
        var member = target.KeyExists
            ? $" \"{SetupCatalog.ServerName}\": {client.Entry}"
            : $" \"{client.ServersKey}\": {{ \"{SetupCatalog.ServerName}\": {client.Entry} }}";
        var insertion = Encoding.UTF8.GetBytes(member + (target.HasMembers ? "," : " "));
        var position = target.OpenBraceOffset + 1;
        return [.. original.AsSpan(0, position), .. insertion, .. original.AsSpan(position)];
    }

    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    private static int BomLength(byte[] json) => json.AsSpan().StartsWith(Utf8Bom) ? Utf8Bom.Length : 0;

    private static InsertionTarget Locate(byte[] json, string serversKey)
    {
        var bomLength = BomLength(json);
        var reader = new Utf8JsonReader(json.AsSpan(bomLength), ReaderOptions);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("the root value is not a JSON object");

        var rootOffset = bomLength + (int)reader.TokenStartIndex;
        var rootHasMembers = false;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            rootHasMembers = true;
            var isServersKey = reader.ValueTextEquals(serversKey);
            reader.Read();
            if (isServersKey)
                return LocateInServers(ref reader, serversKey, bomLength);

            reader.Skip();
        }

        return new InsertionTarget(rootOffset, rootHasMembers, KeyExists: false, IsConfigured: false);
    }

    private static InsertionTarget LocateInServers(ref Utf8JsonReader reader, string serversKey, int bomLength)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException($"\"{serversKey}\" is not a JSON object");

        var offset = bomLength + (int)reader.TokenStartIndex;
        var hasMembers = false;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals(SetupCatalog.ServerName))
                return new InsertionTarget(offset, HasMembers: true, KeyExists: true, IsConfigured: true);

            hasMembers = true;
            reader.Read();
            reader.Skip();
        }

        return new InsertionTarget(offset, hasMembers, KeyExists: true, IsConfigured: false);
    }

    private static void Validate(byte[] json, string serversKey)
    {
        using var document = JsonDocument.Parse(json.AsMemory(BomLength(json)), DocumentOptions);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty(serversKey, out var servers)
            || servers.ValueKind != JsonValueKind.Object
            || !servers.TryGetProperty(SetupCatalog.ServerName, out _))
            throw new JsonException($"the result does not contain \"{serversKey}\".\"{SetupCatalog.ServerName}\"");
    }

    private readonly record struct InsertionTarget(int OpenBraceOffset, bool HasMembers, bool KeyExists, bool IsConfigured);
}

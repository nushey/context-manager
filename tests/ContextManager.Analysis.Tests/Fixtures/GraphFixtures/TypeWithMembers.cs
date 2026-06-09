namespace GraphFixtures;

public class TypeWithMembers
{
    public string Name { get; set; } = string.Empty;

    public string GetGreeting(string input)
    {
        return $"Hello, {input}";
    }
}

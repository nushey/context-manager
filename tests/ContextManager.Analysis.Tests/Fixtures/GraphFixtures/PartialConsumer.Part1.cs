namespace PartialGraphFixtures;

class Dependency { }

partial class Consumer
{
    public Dependency First { get; set; } = new();
}

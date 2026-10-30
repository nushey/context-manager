namespace IncrementalFixtures.Core;

public partial class Envelope
{
    public Sender Channel { get; } = new();
}

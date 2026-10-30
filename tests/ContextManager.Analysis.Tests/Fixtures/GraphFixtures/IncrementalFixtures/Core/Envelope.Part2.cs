namespace IncrementalFixtures.Core;

public partial class Envelope
{
    public void Dispatch()
    {
        Channel.Send("payload");
    }
}

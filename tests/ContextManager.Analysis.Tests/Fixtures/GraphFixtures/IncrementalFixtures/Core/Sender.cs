namespace IncrementalFixtures.Core;

public class Sender
{
    public void Send(object message)
    {
        Audit.Record();
    }
}

using IncrementalFixtures.Core;

namespace IncrementalFixtures.Consumer;

public class Client(Sender sender)
{
    public void Run()
    {
        sender.Send("hello");
    }
}

using System.IO;

namespace ContextFixtures;

public interface IShape
{
    string Name { get; }

    double Area();
}

public class StorageError : IOException, IDisposable
{
    public void Dispose() { }
}

public partial class Order
{
    public class Builder { }
}

public partial class Order { }

public class Invoice
{
    public class Builder { }
}

public class Payload { }

public interface IHandler
{
    void Handle(Payload payload);
}

public class Handler : IHandler
{
    void IHandler.Handle(Payload payload) { }
}

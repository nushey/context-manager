namespace GraphCorrectnessFixtures;

interface IBase<T> { }

class Payload { }

class GenericBase<T> : IBase<T> { }

class Derived : GenericBase<Payload> { }

static class Extensions
{
    public static void Touch<T>(this T value) { }
}

static class Utility
{
    public static void Run() { }
}

class Service
{
    public Service()
    {
        Utility.Run();
    }

    public Payload[] Items
    {
        get
        {
            Utility.Run();
            return [];
        }
    }

    public void Execute(Payload payload)
    {
        payload.Touch();
    }
}

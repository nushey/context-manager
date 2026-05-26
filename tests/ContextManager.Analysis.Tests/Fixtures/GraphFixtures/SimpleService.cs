namespace GraphFixtures;

public class SimpleService : ISimpleService
{
    private readonly ISimpleService _dependency;

    public SimpleService(ISimpleService dependency)
    {
        _dependency = dependency;
    }

    public string Execute(string input)
    {
        return _dependency.Execute(input);
    }
}

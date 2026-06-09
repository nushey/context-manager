namespace ContextManager.Analysis.Tests.Fixtures;

public abstract class AbstractOrderProcessor
{
    public abstract string Process(int orderId);

    public virtual string Describe() => nameof(AbstractOrderProcessor);
}

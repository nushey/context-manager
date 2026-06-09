namespace ContextManager.Analysis.Tests.Fixtures;

public class IndexManager
{
    public virtual void Rebuild() { }
}

// Base type starts with 'I' + lowercase — must be reported as base, not implements
public class IndexManagerCache : IndexManager
{
    public override void Rebuild() { }
}

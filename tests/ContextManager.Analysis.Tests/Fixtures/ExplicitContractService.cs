namespace ContextManager.Analysis.Tests.Fixtures;

public interface IAuditable
{
    void Audit();
    string AuditLabel { get; }
}

public class ExplicitContractService : IAuditable
{
    void IAuditable.Audit() { }

    string IAuditable.AuditLabel => "audit";

    public void Run() { }
}

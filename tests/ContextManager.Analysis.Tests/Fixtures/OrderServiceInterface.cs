using System.Threading;

namespace ContextManager.Analysis.Tests.Fixtures;

public interface IOrderService
{
    string Process(int orderId);
    void Cancel(int orderId, CancellationToken cancellationToken);
}

using System.Threading;
using System.Threading.Tasks;

namespace ContextFixtures;

public abstract class BaseOrderService
{
    public abstract Task<Order> GetOrderAsync(int id, CancellationToken ct);
}

using System.Threading;
using System.Threading.Tasks;

namespace ContextFixtures;

public interface IOrderService
{
    Task<Order> GetOrderAsync(int id, CancellationToken ct);
}

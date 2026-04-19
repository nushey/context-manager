using System.Threading;
using System.Threading.Tasks;

namespace ContextFixtures;

public interface IOrderRepository
{
    Task<Order> FindAsync(int id, CancellationToken ct);
}

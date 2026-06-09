using System.Threading;
using System.Threading.Tasks;

namespace ContextFixtures;

public class OrderAuditor(IOrderRepository repository)
{
    public Task<Order> AuditAsync(int id, CancellationToken ct) => repository.FindAsync(id, ct);
}

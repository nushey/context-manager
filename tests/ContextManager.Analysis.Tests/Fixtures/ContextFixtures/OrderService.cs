using System.Threading;
using System.Threading.Tasks;

namespace ContextFixtures;

public class OrderService : IOrderService
{
    public OrderService(IOrderRepository repository)
    {
        _repository = repository;
    }

    private readonly IOrderRepository _repository;

    public Task<Order> GetOrderAsync(int id, CancellationToken ct) => throw new System.NotImplementedException();
}

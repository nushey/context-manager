using System.Threading;
using System.Threading.Tasks;

namespace ContextFixtures;

public class OrderController
{
    public OrderController(IOrderService service)
    {
        _service = service;
    }

    private readonly IOrderService _service;

    public Task CreateAsync(CreateOrderDto dto, CancellationToken ct) => throw new System.NotImplementedException();
}

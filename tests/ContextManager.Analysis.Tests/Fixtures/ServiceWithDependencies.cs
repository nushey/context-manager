using System.Threading;

namespace ContextManager.Analysis.Tests.Fixtures;

public class ServiceWithDependencies
{
    public ServiceWithDependencies(IOrderRepository orderRepository, IEventBus eventBus)
    {
        _orderRepository = orderRepository;
        _eventBus = eventBus;
    }

    private readonly IOrderRepository _orderRepository;
    private readonly IEventBus _eventBus;

    [Authorize]
    public string ProcessOrder(int orderId, CancellationToken cancellationToken)
    {
        return string.Empty;
    }

    private void InternalHelper()
    {
    }

    public int OrderCount { get; set; }
}

public interface IOrderRepository { }
public interface IEventBus { }

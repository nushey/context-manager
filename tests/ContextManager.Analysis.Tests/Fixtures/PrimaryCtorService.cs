namespace ContextManager.Analysis.Tests.Fixtures;

public class PrimaryCtorService(IOrderRepository orderRepository, IEventBus eventBus)
{
    public string ProcessOrder(int orderId)
    {
        return orderRepository.ToString() + eventBus.ToString() + orderId;
    }
}

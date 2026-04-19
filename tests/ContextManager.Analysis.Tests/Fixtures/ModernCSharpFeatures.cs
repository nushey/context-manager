namespace ContextManager.Analysis.Tests.Fixtures;

// Partial class — isPartial should be true
public partial class PartialOrderService
{
    public string? CustomerName { get; set; }

    public PartialOrderService(string customerName) { CustomerName = customerName; }

    public void Process(string? orderId) { }
}

// Class with required properties — isRequired should be true on the required ones
public class CustomerProfile
{
    public required string Email { get; set; }
    public required string FullName { get; set; }
    public string? PhoneNumber { get; set; }

    public CustomerProfile(string email, string fullName) { Email = email; FullName = fullName; }

    public string GetDisplayName() => FullName;
}

// Class with a generic method with where constraints
public class GenericProcessor
{
    public T Convert<T>(object input) where T : class, new()
    {
        return new T();
    }

    public TResult Map<TSource, TResult>(TSource source) where TSource : notnull where TResult : class
    {
        return default!;
    }
}

// Record with primary constructor — kind should be "record", constructorDependencies from primary params
public record OrderSummary(string OrderId, decimal Total, string? Notes);

using System.Collections.Generic;
using System.Linq;

namespace EdgeCoverageFixtures;

public class Product
{
    public decimal Price { get; set; }
}

public record ProductResponse(int Id, decimal Price);

public interface IStrategy
{
    string Name { get; }
}

public class StandardStrategy : IStrategy
{
    public string Name => "standard";
}

public class StrategyHost
{
    private readonly IStrategy _active = new StandardStrategy();

    public string ActiveName() => _active.Name;
}

public class Catalog
{
    public IReadOnlyCollection<ProductResponse> GetAll(IEnumerable<Product> products) =>
        products.Select(ToResponse).ToList();

    public Product? Find() => new Product { Price = 1 };

    private static ProductResponse ToResponse(Product product) => new(0, product.Price);
}

public static class Registrations
{
    public static IEnumerable<Catalog> Register() => Enumerable.Empty<Catalog>();
}

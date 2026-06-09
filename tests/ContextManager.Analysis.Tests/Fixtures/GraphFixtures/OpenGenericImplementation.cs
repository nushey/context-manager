namespace OpenGenericFixtures;

// Open generic interface and an open generic class implementing it.
public interface IStore<T> { }

public class StoreImpl<T> : IStore<T> { }

// User-defined entity used only as a closed type argument.
public class Catalog { }

// Consumer injecting a CLOSED instantiation IStore<Catalog>, and a method
// returning a CLOSED instantiation. Both must collapse to the OPEN IStore<T>.
public class CatalogConsumer(IStore<Catalog> store)
{
    public IStore<Catalog> GetStore()
    {
        return store;
    }
}

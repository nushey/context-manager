namespace ContextFixtures;

public class MemoryCache<TItem> : ICache<TItem> where TItem : class
{
    public TItem? Get(string key) => null;
}

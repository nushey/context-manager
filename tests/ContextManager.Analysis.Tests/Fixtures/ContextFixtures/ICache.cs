namespace ContextFixtures;

public interface ICache<TItem> where TItem : class
{
    TItem? Get(string key);
}

namespace TypeTestFixtures;

public interface IPlugin
{
}

public interface IShippingProvider : IPlugin
{
}

public interface IPaymentProvider : IPlugin
{
}

public interface IAuditedPlugin : IPlugin
{
}

public interface IRepository<TEntity, TKey>
{
}

public interface ITaxProvider : IPlugin
{
}

public interface IPickupProvider : IPlugin
{
}

public interface IMiscProvider : IPlugin
{
}

public class PluginInspector
{
    private const IPlugin NoPlugin = null;

    public int Classify(IPlugin plugin)
    {
        switch (plugin)
        {
            case IShippingProvider shipping:
                return 1;
            default:
                return 0;
        }
    }

    public bool IsPayment(IPlugin plugin) => plugin is IPaymentProvider;

    public bool IsNotAudited(IPlugin plugin) => plugin is not IAuditedPlugin;

    public object RepositoryDefinition() => typeof(IRepository<,>);

    public bool IsTaxProvider(IPlugin plugin) => plugin is ITaxProvider { };

    public bool IsPickupBatch(object plugins) => plugins is IPickupProvider[] or null;

    public bool IsMissing(IPlugin plugin) => plugin switch { NoPlugin => true, _ => false };

    public bool IsMisc(IPlugin plugin)
    {
        switch (plugin)
        {
            case IMiscProvider:
                return true;
            default:
                return false;
        }
    }

    public bool IsMissingLabel(IPlugin plugin)
    {
        switch (plugin)
        {
            case NoPlugin:
                return true;
            default:
                return false;
        }
    }
}

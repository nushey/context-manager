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

public class PluginInspector
{
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
}

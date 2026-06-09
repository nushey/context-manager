using System.Collections.Generic;
using System.Threading.Tasks;

namespace ContextFixtures;

public class BclHeavyService(string connection, int? retries)
{
    public void Sync(Task<string> pending, List<string> keys, bool force)
    {
    }

    public void Register(UnknownPolicy policy, string name)
    {
    }
}

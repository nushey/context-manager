using System.Threading.Tasks;

namespace ContextManager.Analysis.Tests.Fixtures;

public abstract class MemberDetailShowcase
{
    public int GetSet { get; set; }
    public int GetInit { get; init; }
    public int GetPrivateSet { get; private set; }
    public int ExpressionBodied => GetSet * 2;

    public MemberDetailShowcase(int seed) { GetSet = seed; }

    public async Task<string> LoadAsync() => await Task.FromResult("done");

    public static async Task<int> CountAsync() => await Task.FromResult(0);

    public override string ToString() => nameof(MemberDetailShowcase);

    public virtual void Extend() { }

    public abstract void MustImplement();

    public void Plain() { }
}

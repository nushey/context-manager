namespace ContextManager.Analysis.Tests.Fixtures;

public interface IEntity
{
    int Id { get; }
}

// Generic interface with a type-level constraint
public interface IRepository<TEntity> where TEntity : IEntity
{
    TEntity? FindById(int id);
}

// Generic class with multiple constraints implementing a generic interface
public class GenericRepository<TEntity> : IRepository<TEntity> where TEntity : class, IEntity, new()
{
    public TEntity? FindById(int id) => null;

    public void Save(TEntity entity) { }
}

// Multiple type parameters with multiple constraint clauses (declaration order matters)
public class EntityMapper<TSource, TResult>
    where TSource : notnull
    where TResult : class
{
    public TResult Map(TSource source) => default!;
}

// DTO suffix heuristic must see the bare identifier "PagedResponse", not "PagedResponse<T>".
// The parameterized ctor defeats the auto-property branch so only the suffix branch fires.
public class PagedResponse<T>
{
    public int Page { get; set; }

    public PagedResponse(int page) { Page = page; }

    public void Reset() { }
}

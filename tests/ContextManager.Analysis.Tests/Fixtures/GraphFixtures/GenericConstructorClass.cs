namespace TestFixtures;

public interface IRepository<T> { }
public class MyEntity { }
public class MyRepositoryConsumer(IRepository<MyEntity> repository) { }

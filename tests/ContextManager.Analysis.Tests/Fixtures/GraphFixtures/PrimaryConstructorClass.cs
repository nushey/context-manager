namespace TestFixtures;

public interface IMyService { }

public class MyServiceConsumer(IMyService service) { }

namespace SearchFixtures.Authentication
{
    public interface ISignIn
    {
        void SignIn();
    }

    public class Session : ISignIn
    {
        private void Login() { }
        public void Login(int value) { }
        public T ConvertValue<T>(T value) => value;
        public void Receive(ParameterMarker value) { }
        void ISignIn.SignIn() { }
    }

    public class ParameterMarker { }
}

namespace SearchFixtures.Second
{
    public class Other
    {
        public void FindLogin() { }
    }
}

namespace SearchFixtures.MetadataOnly
{
    public enum Color { Red }
}

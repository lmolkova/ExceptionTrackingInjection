using Xunit;

namespace Microsoft.ApplicationInsights.ExceptionTracking.EmptyWebApp.Tests
{
    public class EmptyWebAppInjectionTests
    {
        [Fact]
        public void InjectionDoesNotThrow()
        {
            Injector.ForceInject();
        }
    }
}

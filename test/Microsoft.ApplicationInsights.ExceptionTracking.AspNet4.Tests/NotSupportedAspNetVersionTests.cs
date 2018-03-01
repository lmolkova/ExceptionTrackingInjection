using System.Linq;
using System.Web.Http;
using System.Web.Mvc;
using Xunit;

namespace Microsoft.ApplicationInsights.ExceptionTracking.AspNet4.Tests
{
    public class NotSupportedAspNetVersionTests
    {
        public NotSupportedAspNetVersionTests()
        {
            GlobalConfiguration.Configuration.Filters.Clear();
            GlobalFilters.Filters.Clear();
        }

        [Fact]
        public void InjectionNoopOnAspNet4()
        {
            Injector.ForceInject();
            Assert.False(GlobalFilters.Filters.Any());
            Assert.False(GlobalConfiguration.Configuration.Filters.Any());
        }

    }
}

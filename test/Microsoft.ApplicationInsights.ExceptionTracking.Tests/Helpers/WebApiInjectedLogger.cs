using System.Web.Http.ExceptionHandling;

namespace Microsoft.ApplicationInsights.ExceptionTracking.Tests.Helpers
{
    public class WebApiInjectedLogger : ExceptionLogger
    {
        public const bool IsAutoInjected = true;
    }
}

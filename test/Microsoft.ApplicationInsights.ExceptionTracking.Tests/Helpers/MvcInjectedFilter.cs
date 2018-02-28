using System.Web.Mvc;

namespace Microsoft.ApplicationInsights.ExceptionTracking.Tests.Helpers
{
    class MvcInjectedFilter : HandleErrorAttribute
    {
        public const bool IsAutoInjected = true;
    }
}

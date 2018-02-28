using System.Globalization;
using System.IO;
using System.Threading;
using System.Web;
using System.Web.Hosting;
using System.Web.Mvc;
using Moq;

namespace Microsoft.ApplicationInsights.ExceptionTracking.Tests.Helpers
{
    class AspNetHelper
    {
        public static HttpContextBase GetFakeHttpContext(bool isCustomErrorEnabed)
        {
            Thread.GetDomain().SetData(".appPath", string.Empty);
            Thread.GetDomain().SetData(".appVPath", string.Empty);
            using (var writerRequest = new StringWriter(CultureInfo.InvariantCulture))
            using (var writerResponse = new StringWriter(CultureInfo.InvariantCulture))
            {
                var workerRequest = new SimpleWorkerRequest("page", "", writerRequest);
                var response = new HttpResponseWrapper(new HttpResponse(writerResponse));
                var mock = new Mock<HttpContextWrapper>(new HttpContext(workerRequest));
                mock.SetupGet(ctx => ctx.IsCustomErrorEnabled).Returns(isCustomErrorEnabed);
                mock.SetupGet(ctx => ctx.Response).Returns(response);
                return mock.Object;
            }
        }

        public static ControllerContext GetFakeControllerContext(bool isCustomErrorEnabed)
        {
            var controllerCtx = new ControllerContext
            {
                HttpContext = GetFakeHttpContext(isCustomErrorEnabed)
            };

            controllerCtx.RouteData.Values["controller"] = "controller";
            controllerCtx.RouteData.Values["action"] = "action";
            controllerCtx.Controller = new DefaultController();

            return controllerCtx;
        }

        public class DefaultController : Controller
        {
        }
    }
}

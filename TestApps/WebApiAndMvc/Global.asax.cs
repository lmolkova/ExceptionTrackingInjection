using System;
using System.Diagnostics;
using System.Reflection;
using System.Web;
using System.Web.Mvc;
using System.Web.Routing;
using System.Web.Http;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.ExceptionTracking;

namespace ErrorHandlers
{
    public class Global : HttpApplication
    {
        TelemetryClient ai = new TelemetryClient(); // or re-use an existing instance
        void Application_Start(object sender, EventArgs e)
        {
            Injector.Inject();
            //AppDomain.CurrentDomain.AssemblyResolve += MyAssemblyResolve;

            // Code that runs on application startup
            AreaRegistration.RegisterAllAreas();
            GlobalConfiguration.Configure(WebApiConfig.Register);
            RouteConfig.RegisterRoutes(RouteTable.Routes);
        }

        void Application_Error(object sender, EventArgs e)
        {
            if (HttpContext.Current.IsCustomErrorEnabled && Server.GetLastError() != null)
            {
                ai.TrackException(Server.GetLastError());
            }
        }

        private static Assembly MyAssemblyResolve(object sender, ResolveEventArgs args)
        {
            Debug.WriteLine(args.Name);

            Assembly asm = null;
            try
            {
                if (args.Name.ToLowerInvariant().Contains("system.web.http"))
                {
                    if (args.Name.ToLowerInvariant().Contains("webhost"))
                    {
                        asm = Assembly.GetAssembly(
                            Type.GetType("System.Web.Http.GlobalConfiguration, System.Web.Http.WebHost"));
                    }
                    else
                    {
                        asm = Assembly.GetAssembly(
                            Type.GetType("System.Web.Http.ExceptionHandling.ExceptionLogger, System.Web.Http"));
                    }
                }
                else if (args.Name.ToLowerInvariant().Contains("system.web.mvc"))
                {
                    asm = Assembly.GetAssembly(Type.GetType("System.Web.Mvc.HandleErrorAttribute, System.Web.Mvc"));
                }
            }
            catch (Exception e)
            {
                Debug.WriteLine(e.ToString());
            }

            return asm;
        }
    }

    public class ExceptionHttpModule : IHttpModule
    {
        /// <inheritdoc />
        public void Dispose()
        {
        }

        /// <inheritdoc />
        public void Init(HttpApplication context)
        {

        }
    }
}
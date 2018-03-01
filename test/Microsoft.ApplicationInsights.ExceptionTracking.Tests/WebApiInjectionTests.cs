using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Web.Http;
using System.Web.Http.ExceptionHandling;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.ExceptionTracking.Tests.Helpers;
using Microsoft.ApplicationInsights.Extensibility;
using Xunit;

namespace Microsoft.ApplicationInsights.ExceptionTracking.Tests
{
    public class WebApiInjectionTests : IDisposable
    {
        private readonly ConcurrentQueue<ITelemetry> sentTelemetry;
        public WebApiInjectionTests()
        {
            GlobalConfiguration.Configuration.Services.Clear(typeof(IExceptionLogger));
            sentTelemetry = new ConcurrentQueue<ITelemetry>();

            var channel = new StubTelemetryChannel
            {
                OnSend = t =>
                {
                    if (t is ExceptionTelemetry) sentTelemetry.Enqueue(t);
                }
            };

            TelemetryConfiguration.Active.TelemetryChannel = channel;
        }

        [Fact]
        public void WebApiExceptionLoggerIsInjectedAndTracksException()
        {
            Assert.False(GlobalConfiguration.Configuration.Services.GetServices(typeof(IExceptionLogger)).Any());

            Injector.ForceInject();

            var webApiExceptionLoggers = GlobalConfiguration.Configuration.Services.GetServices(typeof(IExceptionLogger)).ToList();
            Assert.Single(webApiExceptionLoggers);

            var logger = (ExceptionLogger)webApiExceptionLoggers[0];
            Assert.NotNull(logger);

            var exception = new Exception("test");

            var exceptionContext = new ExceptionLoggerContext(new ExceptionContext(exception, new ExceptionContextCatchBlock("catch block name", true, false)));
            logger.Log(exceptionContext);

            Assert.Single(sentTelemetry);

            var trackedException = (ExceptionTelemetry)sentTelemetry.Single();
            Assert.NotNull(trackedException);
            Assert.Equal(exception, trackedException.Exception);
        }

        [Fact]
        public void WebApiExceptionLoggerIsNotInjectedIfAnotherInjectionDetected()
        {
            GlobalConfiguration.Configuration.Services.Add(typeof(IExceptionLogger), new WebApiInjectedLogger());
            Assert.Single(GlobalConfiguration.Configuration.Services.GetServices(typeof(IExceptionLogger)));

            Injector.ForceInject();

            var loggers = GlobalConfiguration.Configuration.Services.GetServices(typeof(IExceptionLogger)).ToList();
            Assert.Single(loggers);
            Assert.IsType<WebApiInjectedLogger>(loggers.Single());
        }

        public void Dispose()
        {
            while (sentTelemetry.TryDequeue(out var _))
            {
            }

            GlobalConfiguration.Configuration.Services.Clear(typeof(IExceptionLogger));
        }
    }
}

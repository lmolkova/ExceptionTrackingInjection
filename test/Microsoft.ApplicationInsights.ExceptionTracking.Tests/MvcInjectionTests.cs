using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Web.Mvc;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.ExceptionTracking.Tests.Helpers;
using Microsoft.ApplicationInsights.Extensibility;
using Xunit;

namespace Microsoft.ApplicationInsights.ExceptionTracking.Tests
{
    public class MvcInjectionTests : IDisposable
    {
        private readonly ConcurrentQueue<ITelemetry> sentTelemetry;

        public MvcInjectionTests()
        {
            GlobalFilters.Filters.Clear();
            sentTelemetry = new ConcurrentQueue<ITelemetry>();

            var channel = new StubTelemetryChannel { OnSend = t =>
                {
                    if (t is ExceptionTelemetry) sentTelemetry.Enqueue(t);
                }
            };

            TelemetryConfiguration.Active.TelemetryChannel = channel;
        }

        [Fact]
        public void MvcExceptionFilterIsInjectedAndTracksException()
        {
            Injector.InjectInternal();

            var mvcExceptionFilters = GlobalFilters.Filters;
            Assert.Single(mvcExceptionFilters);

            var handleExceptionFilter = (HandleErrorAttribute)mvcExceptionFilters.Single().Instance;
            Assert.NotNull(handleExceptionFilter);

            var exception = new Exception("test");
            var controllerCtx = AspNetHelper.GetFakeControllerContext(isCustomErrorEnabed: true);
            handleExceptionFilter.OnException(new ExceptionContext(controllerCtx, exception));

            Assert.Single(sentTelemetry);

            var trackedException = (ExceptionTelemetry)sentTelemetry.Single();
            Assert.NotNull(trackedException);
            Assert.Equal(exception, trackedException.Exception);
        }

        [Fact]
        public void MvcExceptionLoggerIsNotInjectedIfAnotherInjectionDetected()
        {
            GlobalFilters.Filters.Add(new MvcInjectedFilter());
            Assert.Single(GlobalFilters.Filters);

            Injector.InjectInternal();

            var filters = GlobalFilters.Filters;
            Assert.Single(filters);
            Assert.IsType<MvcInjectedFilter>(filters.Single().Instance);
        }

        [Fact]
        public void MvcExceptionFilterNoopIfCustomErrorsIsFalse()
        {
            Injector.InjectInternal();

            var mvcExceptionFilters = GlobalFilters.Filters;
            Assert.Single(mvcExceptionFilters);

            var handleExceptionFilter = (HandleErrorAttribute)mvcExceptionFilters.Single().Instance;
            Assert.NotNull(handleExceptionFilter);

            var exception = new Exception("test");
            var controllerCtx = AspNetHelper.GetFakeControllerContext(isCustomErrorEnabed: false);
            handleExceptionFilter.OnException(new ExceptionContext(controllerCtx, exception));

            Assert.False(sentTelemetry.Any());
        }

        [Fact]
        public void MvcExceptionFilterNoopIfExceptionIsNull()
        {
            Injector.InjectInternal();

            var mvcExceptionFilters = GlobalFilters.Filters;
            Assert.Single(mvcExceptionFilters);

            var handleExceptionFilter = (HandleErrorAttribute)mvcExceptionFilters.Single().Instance;
            Assert.NotNull(handleExceptionFilter);

            var controllerCtx = AspNetHelper.GetFakeControllerContext(isCustomErrorEnabed: true);
            var exceptionContext = new ExceptionContext(controllerCtx, new Exception());
            exceptionContext.Exception = null;
            handleExceptionFilter.OnException(exceptionContext);

            Assert.False(sentTelemetry.Any());
        }

        public void Dispose()
        {
            while (sentTelemetry.TryDequeue(out var _))
            {
            }

            GlobalFilters.Filters.Clear();
        }
    }
}

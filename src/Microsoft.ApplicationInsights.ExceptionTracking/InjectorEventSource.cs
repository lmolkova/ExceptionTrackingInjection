using System;
using System.Diagnostics.Tracing;

namespace Microsoft.ApplicationInsights.ExceptionTracking
{
    [EventSource(Name = "Microsoft-ApplicationInsights-Extensibility-Injector")]
    internal sealed class InjectorEventSource : EventSource
    {
        /// <summary>
        /// Instance of the PlatformEventSource class.
        /// </summary>
        public static readonly InjectorEventSource Log = new InjectorEventSource();

        private InjectorEventSource()
        {
            this.ApplicationName = this.GetApplicationName();
        }

        public string ApplicationName { [NonEvent] get; [NonEvent] private set; }

        [Event(1,
            Keywords = Keywords.Diagnostics,
            Message = "Injection started.",
            Level = EventLevel.Verbose)]
        public void InjectionStarted(string appDomainName = "Incorrect")
        {
            this.WriteEvent(1, this.ApplicationName);
        }

        [Event(2,
            Keywords = Keywords.Diagnostics,
            Message = "{0} Injection failed. Error message: {1}",
            Level = EventLevel.Error)]
        public void InjectionFailed(string component, string error, string appDomainName = "Incorrect")
        {
            this.WriteEvent(2, component ?? string.Empty, error ?? string.Empty, this.ApplicationName);
        }

        [Event(3,
            Keywords = Keywords.Diagnostics,
            Message = "Version '{0}' of component '{1}' is not supported",
            Level = EventLevel.Error)]
        public void VersionNotSupported(string version, string component, string appDomainName = "Incorrect")
        {
            this.WriteEvent(3, version ?? string.Empty, component ?? string.Empty, this.ApplicationName);
        }

        [Event(
            4,
            Keywords = Keywords.Diagnostics,
            Message = "Unknown exception. Error message: {0}.",
            Level = EventLevel.Error)]
        public void UnknownError(string error, string appDomainName = "Incorrect")
        {
            this.WriteEvent(4, error ?? string.Empty, this.ApplicationName);
        }

        [Event(
            5,
            Keywords = Keywords.Diagnostics,
            Message = "Another exception filter or logger is already injected. Type: '{0}', component: '{1}'",
            Level = EventLevel.Verbose)]
        public void AlreadyInjected(string type, string component, string appDomainName = "Incorrect")
        {
            this.WriteEvent(5, type ?? string.Empty, component ?? string.Empty, this.ApplicationName);
        }

        [Event(6,
            Keywords = Keywords.Diagnostics,
            Message = "Injection completed.",
            Level = EventLevel.Verbose)]
        public void InjectionCompleted(string appDomainName = "Incorrect")
        {
            this.WriteEvent(6, this.ApplicationName);
        }

        [NonEvent]
        private string GetApplicationName()
        {
            string name;
            try
            {
                name = AppDomain.CurrentDomain.FriendlyName;
            }
            catch (Exception exp)
            {
                name = "Undefined " + exp.Message;
            }

            return name;
        }

        public sealed class Keywords
        {
            /// <summary>
            /// Key word for diagnostics events.
            /// </summary>
            public const EventKeywords Diagnostics = (EventKeywords)0x2;
        }
    }
}

# ExceptionTrackingInjection

This package dynamically injects ApplicationInsight excepton tracking into the ASP.NET application.
It substitutes manual [MVC and WebApi](https://docs.microsoft.com/en-us/azure/application-insights/app-insights-asp-net-exceptions#mvc) steps to track exceptions.

## Usage

**Warning** This module is intended to be called by ApplicationInsights SDK. Example provided for test purposes only.

Add following line into `Application_Start` or `IHttpModule.Init`
```csharp
Microsoft.ApplicationInsights.ExceptionTracking.Injector.Inject();
```
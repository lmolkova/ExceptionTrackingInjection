using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace Microsoft.ApplicationInsights.ExceptionTracking
{
    public class Injector
    {
        // we only support ASP.NET 5 for now 
        // and may extend list of supported versions if there will be a business need 
        private const int MinimumMvcVersion = 5;
        private const int MinimumWebApiVersion = 5;

        private const string AssemblyName = "Microsoft.ApplicationInsights.ExceptionTracking";
        private const string MvcHandlerName = AssemblyName + ".MvcExceptionFilter";
        private const string WebApiHandlerName = AssemblyName + ".WebApiExceptionLogger";

        private const string TelemetryClientFieldName = "telemetryClient";
        private const string IsAutoInjectedFieldName = "IsAutoInjected";
        private const string OnExceptionMethodName = "OnException";
        private const string OnLogMethodName = "Log";

        private static volatile bool init = false;
        private static readonly object syncObject = new object();

        /// <summary>
        /// Injects MVC5 exception filter and WebApi2 exception logger into the global configurations.
        /// </summary>
        /// <remarks>
        /// <para>Injection is done on the first time being called and becomes noop on consecutive runs.</para>
        /// <para>If MVC or WebApi are not used by the application, they are ignored.</para>
        /// <para>As method modifies global configurations, it affects IIS-hosted ASP.NET applications. Owin self hosted applications are not supported</para>
        /// </remarks>
        public static void Inject()
        {
            if (!init)
            {
                lock (syncObject)
                {
                    if (!init)
                    {
                        InjectInternal();
                        init = true;
                    }
                }
            }
        }

        /// <summary>
        /// Forces injection of MVC5 exception filter and WebApi2 exception logger into the global configurations.
        /// </summary>
        /// <remarks>
        /// <para>Injection is attempted each time method is called. However if the filter/logger was injected already, injection is skipped.</para>
        /// <para>Use this method only when you can guarantee it's called once per AppDomain.</para>
        /// </remarks>
        [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
        public static void ForceInject()
        {
            InjectInternal();
            init = true;
        }

        private static void InjectInternal()
        {
            try
            {
                InjectorEventSource.Log.InjectionStarted();

                var telemetryClientType = GetTypeOrFail("Microsoft.ApplicationInsights.TelemetryClient, Microsoft.ApplicationInsights");
                var exceptionTelemetryType = GetTypeOrFail("Microsoft.ApplicationInsights.DataContracts.ExceptionTelemetry, Microsoft.ApplicationInsights");
                var trackExceptionMethod = GetMethodOrFail(telemetryClientType, "TrackException", new[] { exceptionTelemetryType });
                var exceptionTelemetryCtor = GetConstructorOrFail(exceptionTelemetryType, new[] { typeof(Exception) });

                var telemetryClient = Activator.CreateInstance(telemetryClientType);
                var assemblyName = new AssemblyName(AssemblyName);
                var assemblyBuilder = AppDomain.CurrentDomain.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.Run);

                var moduleBuilder = assemblyBuilder.DefineDynamicModule(AssemblyName);

                AddMvcFilter(telemetryClient,
                    telemetryClientType,
                    moduleBuilder,
                    exceptionTelemetryCtor,
                    trackExceptionMethod);

                AddWebApiExceptionLogger(telemetryClient,
                    telemetryClientType,
                    moduleBuilder,
                    exceptionTelemetryCtor,
                    trackExceptionMethod);

            }
            catch (Exception e)
            {
                InjectorEventSource.Log.UnknownError(e.ToString());
            }

            InjectorEventSource.Log.InjectionCompleted();
        }

        #region Mvc

        /// <summary>
        /// Generates new Mvc5 filter class implementing <see cref="System.Web.Mvc.HandleErrorAttribute"/> and adds instance of it to the <see cref="GlobalFilterCollection"/>
        /// </summary>
        /// <param name="telemetryClient"><see cref="Microsoft.ApplicationInsights.TelemetryClient"/> instance.</param>
        /// <param name="telemetryClientType">Type of <see cref="Microsoft.ApplicationInsights.TelemetryClient"/>.</param>
        /// <param name="moduleBuilder"><see cref="ModuleBuilder"/> to define type in.</param>
        /// <param name="exceptionTelemetryCtor"><see cref="ConstructorInfo"/> of default constructor of <see cref="Microsoft.ApplicationInsights.DataContracts.ExceptionTelemetry"/>.</param>
        /// <param name="trackExceptionMethod"><see cref="MethodInfo"/> of <see cref="Microsoft.ApplicationInsights.TelemetryClient.TrackException"/></param>
        /// <remarks>
        /// This method emits following code:
        /// [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true, AllowMultiple = true)]
        /// public class MvcExceptionFilter : HandleErrorAttribute
        /// {
        ///     public const bool IsAutoInjected = true;
        ///     private readonly TelemetryClient telemetryClient = new TelemetryClient();
        ///     
        ///     public MvcExceptionFilter(TelemetryClient tc) : base()
        ///     {
        ///         telemetryClient = tc;
        ///     }
        ///     
        ///     public override void OnException(ExceptionContext filterContext)
        ///     {
        ///        if (filterContext != null && filterContext.HttpContext != null && filterContext.Exception != null && filterContext.HttpContext.IsCustomErrorEnabled)
        ///            telemetryClient.TrackException(new ExceptionTelemetry(filterContext.Exception));
        ///        base.OnException(filterContext);
        ///     }
        /// }
        /// 
        ///  GlobalFilters.Filters.Add(new MvcExceptionFilter(new TelemetryClient()));
        ///</remarks>
        private static void AddMvcFilter(dynamic telemetryClient,
            Type telemetryClientType,
            ModuleBuilder moduleBuilder,
            ConstructorInfo exceptionTelemetryCtor,
            MethodInfo trackExceptionMethod)
        {
            try
            {
                // Get HandleErrorAttribute, make sure it's resolved and MVC version is supported
                var handleErrorType = GetTypeOrFail("System.Web.Mvc.HandleErrorAttribute, System.Web.Mvc");
                if (handleErrorType.Assembly.GetName().Version.Major < MinimumMvcVersion)
                {
                    InjectorEventSource.Log.VersionNotSupported(handleErrorType.Assembly.GetName().Version.ToString(), "MVC");
                    return;
                }

                // get global filter collection
                GetMvcGlobalFiltersOrFail(out dynamic globalFilters, out Type globalFilterCollectionType);

                if (!NeedToInjectMvc(globalFilters))
                {
                    // there is another filter in the collection that has IsAutoInjected const field set to true.
                    // perhaps it's injected by AppInsights SDK and we should stop.
                    return;
                }

                var exceptionContextType = GetTypeOrFail("System.Web.Mvc.ExceptionContext, System.Web.Mvc");
                var exceptionGetter = GetMethodOrFail(exceptionContextType, "get_Exception");

                var controllerContextType = GetTypeOrFail("System.Web.Mvc.ControllerContext, System.Web.Mvc");
                var httpContextGetter = GetMethodOrFail(controllerContextType, "get_HttpContext");

                var baseOnException = GetMethodOrFail(handleErrorType, OnExceptionMethodName, new[] {exceptionContextType});
                var addFilter = GetMethodOrFail(globalFilterCollectionType, "Add", new[] {typeof(object)});

                // HttpContextBase requires full assembly name to be resolved
                // even though version 4.0.0.0 (CLR version) is specified, it will be resolved to the latest .NET System.Web installed
                var httpContextBaseType = GetTypeOrFail("System.Web.HttpContextBase, System.Web, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a");
                var isCustomErrorEnabled = GetMethodOrFail(httpContextBaseType, "get_IsCustomErrorEnabled");

                // build a type for exception filter.
                TypeBuilder typeBuilder = moduleBuilder.DefineType(MvcHandlerName, TypeAttributes.Public | TypeAttributes.Class, handleErrorType);
                typeBuilder.SetCustomAttribute(GetUsageAttributeOrFail());
                var tcField = typeBuilder.DefineField(TelemetryClientFieldName, telemetryClientType, FieldAttributes.Private);

                // add IsAutoInjected field so let AppInsights SDK know that no other injection is needed.
                DefineAutoInjectedField(typeBuilder);

                // emit constructor that assigns telemetry client field
                var handleErrorBaseCtor = GetConstructorOrFail(handleErrorType, new Type[0]);
                EmitConstructor(typeBuilder, telemetryClientType, tcField, handleErrorBaseCtor);

                // emit OnException method that handles exception
                EmitMvcOnException(typeBuilder,
                    exceptionContextType,
                    tcField,
                    exceptionGetter,
                    trackExceptionMethod,
                    baseOnException,
                    exceptionTelemetryCtor,
                    httpContextBaseType,
                    httpContextGetter,
                    isCustomErrorEnabled);

                // create error handler type
                var handlerType = typeBuilder.CreateType();

                // add handler to global filters
                var mvcFilter = Activator.CreateInstance(handlerType, telemetryClient);
                addFilter.Invoke(globalFilters, new[] {mvcFilter});
            }
            catch (ResolutionException e)
            {
                // some of the required types/methods/properties/etc were not found.
                // it may indicate we are dealing with a new version of MVC library
                // handle it and log here, we may still succeed with WebApi injection
                InjectorEventSource.Log.InjectionFailed("MVC", e.ToString());
            }
        }

        /// <summary>
        /// Emits OnException method:
        ///     public override void OnException(ExceptionContext filterContext)
        ///     {
        ///        if (filterContext != null && filterContext.HttpContext != null && filterContext.Exception != null && filterContext.HttpContext.IsCustomErrorEnabled)
        ///            telemetryClient.TrackException(new ExceptionTelemetry(filterContext.Exception));
        ///        base.OnException(filterContext);
        ///     }       
        /// </summary>
        /// <param name="typeBuilder">MvcExceptionFilter type builder.</param>
        /// <param name="exceptionContextType">Type of ExceptionContext </param>
        /// <param name="tcField">FieldInfo of MvcExceptionFilter.telemetryClient</param>
        /// <param name="exceptionGetter">MethodInfo to get ExceptionContext.Exception</param>
        /// <param name="trackException">MethodInfo of TelemetryClient.TrackException(ExceptionTelemetry)</param>
        /// <param name="baseOnException">MethodInfo of base (HandleErrorAttribute) OnException method</param>
        /// <param name="exceptionTelemetryCtor">ConstructorInfo of ExceptionTelemetry</param>
        /// <param name="httpContextBaseType">Type of HttpContextBase</param>
        /// <param name="httpContextGetter">MethodInfo to get ExceptionContext.HttpContext</param>
        /// <param name="isCustomErrorEnabled">MethodInfo to get ExceptionContext.HttpContextBase.IsCustomErrorEnabled</param>
        private static void EmitMvcOnException(
            TypeBuilder typeBuilder,
            Type exceptionContextType,
            FieldInfo tcField,
            MethodInfo exceptionGetter,
            MethodInfo trackException,
            MethodInfo baseOnException,
            ConstructorInfo exceptionTelemetryCtor,
            Type httpContextBaseType,
            MethodInfo httpContextGetter,
            MethodInfo isCustomErrorEnabled)
        {

            // defines public override void OnException(ExceptionContext filterContext)
            var onException = typeBuilder.DefineMethod(OnExceptionMethodName,
                MethodAttributes.Public | MethodAttributes.ReuseSlot |
                MethodAttributes.Virtual | MethodAttributes.HideBySig,
                null,
                new[] { exceptionContextType });
            var il = onException.GetILGenerator();

            Label track = il.DefineLabel();
            Label end = il.DefineLabel();
            Label n1 = il.DefineLabel();
            Label n2 = il.DefineLabel();
            Label n3 = il.DefineLabel();

            var httpContext = il.DeclareLocal(httpContextBaseType);
            var exception = il.DeclareLocal(typeof(Exception));
            var v2 = il.DeclareLocal(typeof(bool));
            var v3 = il.DeclareLocal(typeof(bool));
            var v4 = il.DeclareLocal(typeof(bool));
            var v5 = il.DeclareLocal(typeof(bool));

            // if filterContext is null, goto the end
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Ceq);
            il.Emit(OpCodes.Stloc, v2);
            il.Emit(OpCodes.Ldloc, v2);
            il.Emit(OpCodes.Brfalse_S, n1);
            il.Emit(OpCodes.Br_S, end);

            // if filterContext.HttpContext is null, goto the end
            il.MarkLabel(n1);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Callvirt, httpContextGetter);
            il.Emit(OpCodes.Stloc, httpContext);
            il.Emit(OpCodes.Ldloc, httpContext);
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Ceq);
            il.Emit(OpCodes.Stloc, v3);
            il.Emit(OpCodes.Ldloc, v3);
            il.Emit(OpCodes.Brfalse_S, n2);
            il.Emit(OpCodes.Br_S, end);

            // if filterContext.Exception is null, goto the end
            il.MarkLabel(n2);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Callvirt, exceptionGetter);
            il.Emit(OpCodes.Stloc, exception);
            il.Emit(OpCodes.Ldloc, exception);
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Ceq);
            il.Emit(OpCodes.Stloc_S, v4);
            il.Emit(OpCodes.Ldloc_S, v4);
            il.Emit(OpCodes.Brfalse_S, n3);
            il.Emit(OpCodes.Br_S, end);

            // if filterContext.HttpContext.IsCustomErrorEnabled is false, goto the end
            il.MarkLabel(n3);
            il.Emit(OpCodes.Ldloc, httpContext);
            il.Emit(OpCodes.Callvirt, isCustomErrorEnabled);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Ceq);
            il.Emit(OpCodes.Stloc_S, v5);
            il.Emit(OpCodes.Ldloc_S, v5);
            il.Emit(OpCodes.Brfalse_S, track);
            il.Emit(OpCodes.Br_S, end);

            // telemetryClient.TrackException(new ExceptionTelemetry(filterContext.Exception))
            il.MarkLabel(track);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, tcField);
            il.Emit(OpCodes.Ldloc, exception);
            il.Emit(OpCodes.Newobj, exceptionTelemetryCtor);
            il.Emit(OpCodes.Callvirt, trackException);
            il.Emit(OpCodes.Br_S, end);

            // call base.OnException(filterContext);
            il.MarkLabel(end);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Call, baseOnException);

            il.Emit(OpCodes.Ret);
        }

        /// <summary>
        /// Gets GlobalFilters.Filters property value. If not resolved, or is null, fails with <see cref="ResolutionException"/>.
        /// </summary>
        /// <param name="globalFilters">Resolved GlobalFilters.Filters instance.</param>
        /// <param name="globalFilterCollectionType">Resolved GlobalFilterCollection type</param>
        private static void GetMvcGlobalFiltersOrFail(out dynamic globalFilters, out Type globalFilterCollectionType)
        {
            globalFilterCollectionType = GetTypeOrFail("System.Web.Mvc.GlobalFilterCollection, System.Web.Mvc");

            var globalFiltersType = GetTypeOrFail("System.Web.Mvc.GlobalFilters, System.Web.Mvc");
            globalFilters = GetStaticPropertyValueOrFail(globalFiltersType, "Filters");
        }

        /// <summary>
        /// Checks if another auto injected filter was already added into the filter collection.
        /// </summary>
        /// <param name="globalFilters">GlobalFilters.Filters value of GlobalFilterCollection type.</param>
        /// <returns>True if injection needs to be done, false when collection already contains another auto injected filter</returns>
        private static bool NeedToInjectMvc(dynamic globalFilters)
        {
            var filters = (IEnumerable)globalFilters;
            // GlobalFilterCollection inplements IEnumerable, if it's not fail.
            if (filters == null)
            {
                throw new ResolutionException($"Unexpected type of GlobalFilterCollection {globalFilters.GetType()}");
            }

            var mvcFilterType = GetTypeOrFail("System.Web.Mvc.Filter, System.Web.Mvc");
            var mvcFilterInstanceProp = GetPropertyOrFail(mvcFilterType, "Instance");

            // iterate over the filters
            foreach (var filter in filters)
            {
                if (filter.GetType() != mvcFilterType)
                {
                    throw new ResolutionException($"Unexpected type of MVC Filter {filter.GetType()}");
                }

                var instance = mvcFilterInstanceProp.GetValue(filter);
                if (instance == null)
                {
                    throw new ResolutionException($"MVC Filter does not have Instance property");
                }

                var isAutoInjectedField = instance.GetType().GetField(IsAutoInjectedFieldName, BindingFlags.Public | BindingFlags.Static);
                if (isAutoInjectedField != null && (bool) isAutoInjectedField.GetValue(null))
                {
                    // isAutoInjected public const field (when true) indicates that it's our filter probably injected by AppInsights SDK.
                    // return false and stop MVC injection
                    InjectorEventSource.Log.AlreadyInjected(instance.GetType().AssemblyQualifiedName, "MVC");
                    return false;
                }
            }
            return true;
        }
        #endregion

        #region WebApi

        /// <summary>
        /// Generates new WebApi2 exception logger class implementing ExceptionLogger and adds instance of it to the GlobalConfiguration.Configuration.Services of IExceptionLogger type
        /// </summary>
        /// <param name="telemetryClient"><see cref="Microsoft.ApplicationInsights.TelemetryClient"/> instance.</param>
        /// <param name="telemetryClientType">Type of <see cref="Microsoft.ApplicationInsights.TelemetryClient"/>.</param>
        /// <param name="moduleBuilder"><see cref="ModuleBuilder"/> to define type in.</param>
        /// <param name="exceptionTelemetryCtor"><see cref="ConstructorInfo"/> of default constructor of <see cref="Microsoft.ApplicationInsights.DataContracts.ExceptionTelemetry"/>.</param>
        /// <param name="trackExceptionMethod"><see cref="MethodInfo"/> of <see cref="Microsoft.ApplicationInsights.TelemetryClient.TrackException"/></param>
        /// <remarks>
        /// This method emits following code:
        /// public class WebApiExceptionLogger : ExceptionLogger
        /// {
        ///     public const bool IsAutoInjected = true;
        ///     private readonly TelemetryClient telemetryClient = new TelemetryClient();
        ///     
        ///     public WebApiExceptionLogger(TelemetryClient tc) : base()
        ///     {
        ///         telemetryClient = tc;
        ///     }
        ///     
        ///     public override void OnLog(ExceptionLoggerContext context)
        ///     {
        ///        if (context != null && context.Exception != null)
        ///            telemetryClient.TrackException(new ExceptionTelemetry(context.Exception));
        ///        base.OnLog(context);
        ///     }
        /// }
        /// 
        ///  GlobalConfiguration.Configuration.Services.Add(typeof(IExceptionLogger), new WebApiExceptionFilter(new TelemetryClient()));
        ///</remarks>
        private static void AddWebApiExceptionLogger(
            dynamic telemetryClient,
            Type telemetryClientType,
            ModuleBuilder moduleBuilder,
            ConstructorInfo exceptionTelemetryCtor,
            MethodInfo trackExceptionMethod)
        {
            try
            {
                // try to get all types/methods/properties/fields
                // and if something is not available, fail fast
                var exceptionLoggerType = GetTypeOrFail("System.Web.Http.ExceptionHandling.ExceptionLogger, System.Web.Http");
                if (exceptionLoggerType.Assembly.GetName().Version.Major < MinimumWebApiVersion)
                {
                    InjectorEventSource.Log.VersionNotSupported(exceptionLoggerType.Assembly.GetName().Version.ToString(), "WebApi");
                    return;
                }

                var exceptionContextType = GetTypeOrFail("System.Web.Http.ExceptionHandling.ExceptionLoggerContext, System.Web.Http");
                var iExceptionLoggerType = GetTypeOrFail("System.Web.Http.ExceptionHandling.IExceptionLogger, System.Web.Http");

                // get GlobalConfiguration.Configuration.Services
                GetServicesContainerWebApiOrFail(out dynamic servicesContainer, out Type servicesContainerType);
                if (!NeedToInjectWebApi(servicesContainer, servicesContainerType, iExceptionLoggerType))
                {
                    return;
                }

                var baseOnLog = GetMethodOrFail(exceptionLoggerType, "Log", new[] { exceptionContextType });
                var addLogger = GetMethodOrFail(servicesContainerType, "Add", new[] { typeof(Type), typeof(object) });
                var exceptionGetter = GetMethodOrFail(exceptionContextType, "get_Exception");
                var exceptionLoggerBaseCtor = exceptionLoggerType.GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, CallingConventions.Standard, new Type[0], null);
                if (exceptionLoggerBaseCtor == null)
                {
                    throw new ResolutionException($"Failed to get default constructor for type {exceptionLoggerType.AssemblyQualifiedName}");
                }

                // define 'public class WebApiExceptionLogger : ExceptionLogger' type
                TypeBuilder typeBuilder = moduleBuilder.DefineType(WebApiHandlerName, TypeAttributes.Public | TypeAttributes.Class, exceptionLoggerType);
                DefineAutoInjectedField(typeBuilder);
                var tcField = typeBuilder.DefineField(TelemetryClientFieldName, telemetryClientType, FieldAttributes.Private | FieldAttributes.InitOnly);

                // emit constructor that assigns telemetry client field
                EmitConstructor(typeBuilder, telemetryClientType, tcField, exceptionLoggerBaseCtor);

                // emit Log method 
                EmitWebApiLog(typeBuilder, exceptionContextType, exceptionGetter, tcField, exceptionTelemetryCtor, trackExceptionMethod, baseOnLog);

                // create error WebApiExceptionLogger type
                var aiLoggerType = typeBuilder.CreateType();

                // add WebApiExceptionLogger to list of services
                var aiLogger = Activator.CreateInstance(aiLoggerType, telemetryClient);
                addLogger.Invoke(servicesContainer, new[] { iExceptionLoggerType, aiLogger });
            }
            catch (ResolutionException e)
            {
                // some of the required types/methods/properties/etc were not found.
                // it may indicate we are dealing with a new version of WebApi library
                // handle it and log here, we may still succeed with MVC injection
                InjectorEventSource.Log.InjectionFailed("WebApi", e.ToString());
            }
        }

        /// <summary>
        /// Emits OnLog method:
        ///     public override void OnLog(ExceptionLoggerContext context)
        ///     {
        ///        if (context != null && context.Exception != null)
        ///            telemetryClient.TrackException(new ExceptionTelemetry(context.Exception));
        ///        base.OnLog(context);
        ///     }      
        /// </summary>
        /// <param name="typeBuilder">MvcExceptionFilter type builder.</param>
        /// <param name="exceptionContextType">Type of ExceptionContext </param>
        /// <param name="exceptionGetter">MethodInfo to get ExceptionLoggerContext.Exception</param>
        /// <param name="tcField">FieldInfo of WebApiExceptionFilter.telemetryClient</param>
        /// <param name="exceptionTelemetryCtor">ConstructorInfo of ExceptionTelemetry</param>
        /// <param name="trackException">MethodInfo of TelemetryClient.TrackException(ExceptionTelemetry)</param>
        /// <param name="baseOnLog">MethodInfo of base (ExceptionLogger) OnLog method</param>
        private static void EmitWebApiLog(TypeBuilder typeBuilder, Type exceptionContextType, MethodInfo exceptionGetter, FieldInfo tcField, ConstructorInfo exceptionTelemetryCtor, MethodInfo trackException, MethodInfo baseOnLog)
        {
            // public override void OnLog(ExceptionLoggerContext context)
            var log = typeBuilder.DefineMethod(OnLogMethodName, MethodAttributes.Public | MethodAttributes.ReuseSlot | MethodAttributes.Virtual | MethodAttributes.HideBySig, null, new[] { exceptionContextType });
            var il = log.GetILGenerator();

            Label track = il.DefineLabel();
            Label end = il.DefineLabel();
            Label n1 = il.DefineLabel();

            var exception = il.DeclareLocal(typeof(Exception));
            var v1 = il.DeclareLocal(typeof(bool));
            var v2 = il.DeclareLocal(typeof(bool));

            // is context is null, goto the end
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Ceq);
            il.Emit(OpCodes.Stloc, v1);
            il.Emit(OpCodes.Ldloc, v1);
            il.Emit(OpCodes.Brfalse_S, n1);
            il.Emit(OpCodes.Br_S, end);

            // is context.Exception is null, goto the end
            il.MarkLabel(n1);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Callvirt, exceptionGetter);
            il.Emit(OpCodes.Stloc, exception);
            il.Emit(OpCodes.Ldloc, exception);
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Ceq);
            il.Emit(OpCodes.Stloc, v2);
            il.Emit(OpCodes.Ldloc, v2);
            il.Emit(OpCodes.Brfalse_S, track);
            il.Emit(OpCodes.Br_S, end);

            // telemetryClient.TrackException(new ExceptionTelemetry(context.Exception))
            il.MarkLabel(track);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, tcField);
            il.Emit(OpCodes.Ldloc, exception);
            il.Emit(OpCodes.Newobj, exceptionTelemetryCtor);
            il.Emit(OpCodes.Callvirt, trackException);
            il.Emit(OpCodes.Br_S, end);

            // base.OnLog(context);
            il.MarkLabel(end);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Call, baseOnLog);

            il.Emit(OpCodes.Ret);
        }

        /// <summary>
        /// Gets GlobalConfiguration.Configuration.Services value and type.
        /// </summary>
        /// <param name="serviceContaner">Services collection.</param>
        /// <param name="servicesContainerType">ServicesContainer type of Services</param>
        private static void GetServicesContainerWebApiOrFail(out dynamic serviceContaner, out Type servicesContainerType)
        {
            var globalConfigurationType = GetTypeOrFail("System.Web.Http.GlobalConfiguration, System.Web.Http.WebHost");
            var httpConfigurationType = GetTypeOrFail("System.Web.Http.HttpConfiguration, System.Web.Http");
            servicesContainerType = GetTypeOrFail("System.Web.Http.Controllers.ServicesContainer, System.Web.Http");

            var configuration = GetStaticPropertyValueOrFail(globalConfigurationType, "Configuration");
            serviceContaner = GetPropertyValueOrFail(httpConfigurationType, configuration, "Services");
        }

        /// <summary>
        /// Checks if another auto injected logger was already added into the Services collection.
        /// </summary>
        /// <param name="servicesContainer">GlobalConfiguration.Configuration.Services value</param>
        /// <param name="servicesContainerType">ServicesContainer type</param>
        /// <param name="iExceptionLoggerType">IExceptionLogger type</param>
        /// <returns>True if injection needs to be done, false when collection already contains another auto injected logger</returns>
        private static bool NeedToInjectWebApi(dynamic servicesContainer, Type servicesContainerType, Type iExceptionLoggerType)
        {
            // call ServicesContainer.GetServices(Type) to get collection of all exception loggers
            var getServicesMethod = GetMethodOrFail(servicesContainerType, "GetServices", new Type[] { typeof(Type) });

            var exceptionLoggersObj = getServicesMethod.Invoke(servicesContainer, new object[] { iExceptionLoggerType });
            var exceptionLoggers = (IEnumerable<object>)exceptionLoggersObj;
            if (exceptionLoggers == null)
            {
                throw new ResolutionException($"Unexpected type of {exceptionLoggersObj.GetType()}");
            }

            foreach (var filter in exceptionLoggers)
            {
                var isAutoInjectedField = filter.GetType().GetField(IsAutoInjectedFieldName, BindingFlags.Public | BindingFlags.Static);
                if (isAutoInjectedField != null)
                {
                    var isAutoInjectedFilter = (bool)isAutoInjectedField.GetValue(null);
                    if (isAutoInjectedFilter)
                    {
                        // if logger defines isAutoInjected property, stop WebApi injection.
                        InjectorEventSource.Log.AlreadyInjected(filter.GetType().AssemblyQualifiedName, "WebApi");
                        return false;
                    }
                }
            }
            return true;
        }
        #endregion

        /// <summary>
        /// Emits constructor for Mvc Filter and WebApi logger
        ///     public <Name>(TelemetryClient tc) : base()
        ///     {
        ///         telemetryClient = tc;
        ///     }
        /// </summary>
        /// <param name="typeBuilder">TypeBuilder of MVC filter or WebApi logger.</param>
        /// <param name="telemetryClientType">Type of TelemetryClient.</param>
        /// <param name="field">FieldInfo to assign TelemetryClient instance to.</param>
        /// <param name="baseCtorInfo">ConstructorInfo of the base class.</param>
        /// <returns></returns>
        private static ConstructorBuilder EmitConstructor(TypeBuilder typeBuilder, Type telemetryClientType, FieldInfo field, ConstructorInfo baseCtorInfo)
        {
            // public <Name>(TelemetryClient tc)
            var ctor = typeBuilder.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, new[] { telemetryClientType });
            var il = ctor.GetILGenerator();

            // call base constructor 
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, baseCtorInfo);

            // assign telemetryClient field
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Stfld, field);
            il.Emit(OpCodes.Ret);

            return ctor;
        }

        /// <summary>
        /// Emits IsAutoInjected field. The field is used to mark injected filter/logger and prevent double-injection.
        ///    public const bool IsAutoInjected = true;
        /// </summary>
        /// <param name="exceptionHandlerType"></param>
        /// <returns></returns>
        private static FieldInfo DefineAutoInjectedField(TypeBuilder exceptionHandlerType)
        {
            // we mark our types by using IsAutoInjected field - this is prevention mechanism 
            // this code is shipped as a standalone nuget and eventually may become part of the AppInsigts SDK
            // if it does, AI SDK and lightup might both register filters.
            // all components re-using this code must check for IsAutoInjected on the filter/handler
            // and do not inject itself if there is already a filter with such field
            var field = exceptionHandlerType.DefineField(IsAutoInjectedFieldName, typeof(bool), FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal | FieldAttributes.HasDefault);
            field.SetConstant(true);

            return field;
        }

        #region Helpers

        /// <summary>
        /// Gets attribure builder for AttributeUsageAttribute with AllowMultiple set to true.
        ///    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true, AllowMultiple = true)]
        /// </summary>
        /// <returns>CustomAttributeBuilder for the AttributeUsageAttribute</returns>
        private static CustomAttributeBuilder GetUsageAttributeOrFail()
        {
            var attributeUsageCtor = GetConstructorOrFail(typeof(AttributeUsageAttribute), new[] { typeof(AttributeTargets) });
            var allowMultipleInfo = typeof(AttributeUsageAttribute).GetProperty("AllowMultiple", BindingFlags.Instance | BindingFlags.Public);
            if (attributeUsageCtor == null || allowMultipleInfo == null)
            {
                // must not ever happen 
                throw new ResolutionException($"Failed to get AttributeUsageAttribute ctor or AllowMultiple property");
            }

            return new CustomAttributeBuilder(attributeUsageCtor, new object[] { AttributeTargets.Class | AttributeTargets.Method }, new[] { allowMultipleInfo }, new object[] { true });
        }

        /// <summary>
        /// Gets type by it's name and throws <see cref="ResolutionException"/> if type is not found.
        /// </summary>
        /// <param name="typeName">name of the type to be found. It could be a short namespace qualified type or assembly qualified name, as appropriate.</param>
        /// <returns>Resolved <see cref="Type"/>.</returns>
        private static Type GetTypeOrFail(string typeName)
        {
            var type = Type.GetType(typeName);
            if (type == null)
            {
                throw new ResolutionException($"Failed to get {typeName} type");
            }
            return type;
        }

        /// <summary>
        /// Gets public instance method info from the given type with the given of parameters. Throws <see cref="ResolutionException"/> if method is not found.
        /// </summary>
        /// <param name="type">Type to get method from.</param>
        /// <param name="methodName">Method name</param>
        /// <param name="paramTypes">Array of method parameters. Optional (empty array by default).</param>
        /// <returns>Resolved <see cref="MethodInfo"/></returns>
        private static MethodInfo GetMethodOrFail(Type type, string methodName, Type[] paramTypes = null)
        {
            var method = type.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public, null, paramTypes ?? new Type[0], null);
            if (method == null)
            {
                throw new ResolutionException($"Failed to get {methodName} method from type {type}");
            }
            return method;
        }

        /// <summary>
        /// Gets public instance constructor info from the given type with the given of parameters. Throws <see cref="ResolutionException"/> if constructor is not found.
        /// </summary>
        /// <param name="type">Type to get constructor from.</param>
        /// <param name="paramTypes">Array of constructor parameters.</param>
        /// <returns>Resolved <see cref="ConstructorInfo"/>.</returns>
        private static ConstructorInfo GetConstructorOrFail(Type type, Type[] paramTypes)
        {
            var ctor = type.GetConstructor(paramTypes);
            if (ctor == null)
            {
                throw new ResolutionException($"Failed to get constructor from type {type}");
            }
            return ctor;
        }

        /// <summary>
        /// Gets public instance property info from the given type. Throws <see cref="ResolutionException"/> if property is not found.
        /// </summary>
        /// <param name="type">Type to get property from.</param>
        /// <returns>Resolved <see cref="PropertyInfo"/>.</returns>
        private static PropertyInfo GetPropertyOrFail(Type type, string propertyName)
        {
            var prop = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
            if (prop == null)
            {
                throw new ResolutionException($"Failed to get {propertyName} property info from type {type}");
            }

            return prop;
        }

        /// <summary>
        /// Gets public instance property value from the given type. Throws <see cref="ResolutionException"/> if property is not found.
        /// </summary>
        /// <param name="type">Type to get property from.</param>
        /// <param name="instance">Instance of type to get property value from.</param>
        /// <returns>Value of the property.</returns>
        private static dynamic GetPropertyValueOrFail(Type type, dynamic instance, string propertyName)
        {
            var prop = GetPropertyOrFail(type, propertyName);

            var value = prop.GetValue(instance);
            if (value == null)
            {
                throw new ResolutionException($"Failed to get {propertyName} property value from type {type}");
            }

            return value;
        }

        /// <summary>
        /// Gets public static property value from the given type. Throws <see cref="ResolutionException"/> if property is not found.
        /// </summary>
        /// <param name="type">Type to get property from.</param>
        /// <returns>Value of the property.</returns>
        private static dynamic GetStaticPropertyValueOrFail(Type type, string propertyName)
        {
            var prop = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Static);
            if (prop == null)
            {
                throw new ResolutionException($"Failed to get {propertyName} property info from type {type}");
            }

            var value = prop.GetValue(null);
            if (value == null)
            {
                throw new ResolutionException($"Failed to get {propertyName} property value from type {type}");
            }

            return value;
        }

        /// <summary>
        /// Represents specific resolution exception.
        /// </summary>
        class ResolutionException : Exception
        {
            public ResolutionException(string message) : base(message)
            {
            }
        }

        #endregion
    }
}

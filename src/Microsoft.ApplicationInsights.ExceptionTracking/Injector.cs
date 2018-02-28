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

        private const string AssemblyName = "Microsoft.ApplicationInsights.ExceptionTracker";
        private const string MvcHandlerName = AssemblyName + ".Mvc";
        private const string WebApiHandlerName = AssemblyName + ".WebApi";
        private const string TelemetryClientFieldName = "telemetryClient";
        private const string IsAutoInjectedFieldName = "IsAutoInjected";
        private const string OnExceptionMethodName = "OnException";
        private const string OnLogMethodName = "Log";

        private static volatile bool init = false;
        private static readonly object syncObject = new object();

        public static void Inject()
        {
            if (!init)
            {
                lock (syncObject)
                {
                    if (!init)
                    {
                        try
                        {
                            InjectInternal();
                            init = true;
                        }
                        catch (Exception e)
                        {
                            InjectorEventSource.Log.UnknownError(e.ToString());
                        }
                    }
                }
            }
        }

        internal static void InjectInternal()
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

            InjectorEventSource.Log.InjectionCompleted();
        }

        #region Mvc
        private static void AddMvcFilter(dynamic telemetryClient,
            Type telemetryClientType,
            ModuleBuilder moduleBuilder,
            ConstructorInfo exceptionTelemetryCtor,
            MethodInfo trackExceptionMethod)
        {
            try
            {
                var handleErrorType = GetTypeOrFail("System.Web.Mvc.HandleErrorAttribute, System.Web.Mvc");
                if (handleErrorType.Assembly.GetName().Version.Major < MinimumMvcVersion)
                {
                    InjectorEventSource.Log.VersionNotSupported(handleErrorType.Assembly.GetName().Version.ToString(), "MVC");
                    return;
                }

                GetMvcGlobalFiltersOrFail(out dynamic globalFilters, out Type globalFilterCollectionType);

                if (!NeedToInjectMvc(globalFilters))
                {
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

                TypeBuilder typeBuilder = moduleBuilder.DefineType(MvcHandlerName, TypeAttributes.Public | TypeAttributes.Class, handleErrorType);
                typeBuilder.SetCustomAttribute(GetUsageAttributeOrFail());
                var tcField = typeBuilder.DefineField(TelemetryClientFieldName, telemetryClientType, FieldAttributes.Private);

                DefineAutoInjectedField(typeBuilder);

                // emit constructor that assigns telemetry client field
                var handleErrorBaseCtor = GetConstructorOrFail(handleErrorType, new Type[0]);

                EmitConstructor(typeBuilder, telemetryClientType, tcField, handleErrorBaseCtor);
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
                InjectorEventSource.Log.InjectionFailed("MVC", e.ToString());
            }
        }

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

            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Ceq);
            il.Emit(OpCodes.Stloc, v2);
            il.Emit(OpCodes.Ldloc, v2);
            il.Emit(OpCodes.Brfalse_S, n1);
            il.Emit(OpCodes.Br_S, end);

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

            il.MarkLabel(n3);
            il.Emit(OpCodes.Ldloc, httpContext);
            il.Emit(OpCodes.Callvirt, isCustomErrorEnabled);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Ceq);
            il.Emit(OpCodes.Stloc_S, v5);
            il.Emit(OpCodes.Ldloc_S, v5);
            il.Emit(OpCodes.Brfalse_S, track);
            il.Emit(OpCodes.Br_S, end);

            il.MarkLabel(track);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, tcField);
            il.Emit(OpCodes.Ldloc, exception);
            il.Emit(OpCodes.Newobj, exceptionTelemetryCtor);
            il.Emit(OpCodes.Callvirt, trackException);
            il.Emit(OpCodes.Br_S, end);

            il.MarkLabel(end);

            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Call, baseOnException);

            il.Emit(OpCodes.Ret);
        }

        private static void GetMvcGlobalFiltersOrFail(out dynamic globalFilters, out Type globalFilterCollectionType)
        {
            globalFilterCollectionType = GetTypeOrFail("System.Web.Mvc.GlobalFilterCollection, System.Web.Mvc");

            var globalFiltersType = GetTypeOrFail("System.Web.Mvc.GlobalFilters, System.Web.Mvc");
            globalFilters = GetStaticPropertyValueOrFail(globalFiltersType, "Filters");
        }

        private static bool NeedToInjectMvc(dynamic globalFilters)
        {
            var filters = (IEnumerable)globalFilters;
            if (filters == null)
            {
                throw new ResolutionException($"Unexpected type of GlobalFilterCollection {globalFilters.GetType()}");
            }

            var mvcFilterType = GetTypeOrFail("System.Web.Mvc.Filter, System.Web.Mvc");
            var mvcFilterInstanceProp = GetPropertyOrFail(mvcFilterType, "Instance");

            foreach (var filter in filters)
            {
                if (filter.GetType() != mvcFilterType)
                {
                    continue;
                }

                var instance = mvcFilterInstanceProp.GetValue(filter);
                if (instance == null)
                {
                    continue;
                }
                
                var isAutoInjectedField = instance.GetType().GetField(IsAutoInjectedFieldName, BindingFlags.Public | BindingFlags.Static);
                if (isAutoInjectedField == null || !(bool) isAutoInjectedField.GetValue(null))
                {
                    continue;
                }

                InjectorEventSource.Log.AlreadyInjected(instance.GetType().AssemblyQualifiedName, "MVC");
                return false;
            }
            return true;
        }
        #endregion

        #region WebApi
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

                TypeBuilder typeBuilder = moduleBuilder.DefineType(WebApiHandlerName, TypeAttributes.Public | TypeAttributes.Class, exceptionLoggerType);
                DefineAutoInjectedField(typeBuilder);
                var tcField = typeBuilder.DefineField(TelemetryClientFieldName, telemetryClientType, FieldAttributes.Private | FieldAttributes.InitOnly);

                // emit constructor that assigns telemetry client field
                EmitConstructor(typeBuilder, telemetryClientType, tcField, exceptionLoggerBaseCtor);
                // emit Log method 
                EmitWebApiLog(typeBuilder, exceptionContextType, exceptionGetter, tcField, exceptionTelemetryCtor, trackExceptionMethod, baseOnLog);

                // create error handler type
                var aiLoggerType = typeBuilder.CreateType();

                // add handler to global filters
                var aiLogger = Activator.CreateInstance(aiLoggerType, telemetryClient);
                addLogger.Invoke(servicesContainer, new[] { iExceptionLoggerType, aiLogger });
            }
            catch (ResolutionException e)
            {
                InjectorEventSource.Log.InjectionFailed("WebApi", e.ToString());
            }
        }

        private static MethodBuilder EmitWebApiLog(TypeBuilder typeBuilder, Type exceptionContextType, MethodInfo exceptionGetter, FieldInfo tcField, ConstructorInfo exceptionTelemetryCtor, MethodInfo trackException, MethodInfo baseOnLog)
        {
            var log = typeBuilder.DefineMethod(OnLogMethodName, MethodAttributes.Public | MethodAttributes.ReuseSlot | MethodAttributes.Virtual | MethodAttributes.HideBySig, null, new[] { exceptionContextType });
            var il = log.GetILGenerator();

            Label track = il.DefineLabel();
            Label end = il.DefineLabel();
            Label n1 = il.DefineLabel();

            var exception = il.DeclareLocal(typeof(Exception));
            var v1 = il.DeclareLocal(typeof(bool));
            var v2 = il.DeclareLocal(typeof(bool));

            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Ceq);
            il.Emit(OpCodes.Stloc, v1);
            il.Emit(OpCodes.Ldloc, v1);
            il.Emit(OpCodes.Brfalse_S, n1);
            il.Emit(OpCodes.Br_S, end);

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

            il.MarkLabel(track);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, tcField);
            il.Emit(OpCodes.Ldloc, exception);
            il.Emit(OpCodes.Newobj, exceptionTelemetryCtor);
            il.Emit(OpCodes.Callvirt, trackException);
            il.Emit(OpCodes.Br_S, end);

            il.MarkLabel(end);

            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Call, baseOnLog);

            il.Emit(OpCodes.Ret);

            return log;
        }

        private static void GetServicesContainerWebApiOrFail(out dynamic serviceContaner, out Type servicesContainerType)
        {
            var globalConfigurationType = GetTypeOrFail("System.Web.Http.GlobalConfiguration, System.Web.Http.WebHost");
            var httpConfigurationType = GetTypeOrFail("System.Web.Http.HttpConfiguration, System.Web.Http");
            servicesContainerType = GetTypeOrFail("System.Web.Http.Controllers.ServicesContainer, System.Web.Http");

            var configuration = GetStaticPropertyValueOrFail(globalConfigurationType, "Configuration");
            serviceContaner = GetPropertyValueOrFail(httpConfigurationType, configuration, "Services");
        }

        private static bool NeedToInjectWebApi(dynamic servicesContainer, Type servicesContainerType, Type iExceptionLoggerType)
        {
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
                        InjectorEventSource.Log.AlreadyInjected(filter.GetType().AssemblyQualifiedName, "WebApi");
                        return false;
                    }
                }
            }
            return true;
        }
        #endregion

        private static ConstructorBuilder EmitConstructor(TypeBuilder typeBuilder, Type telemetryClientType, FieldInfo field, ConstructorInfo baseCtorInfo)
        {
            var ctor = typeBuilder.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, new[] { telemetryClientType });
            var il = ctor.GetILGenerator();
            il = ctor.GetILGenerator();

            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, baseCtorInfo);

            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Stfld, field);
            il.Emit(OpCodes.Ret);

            return ctor;
        }

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

        private static CustomAttributeBuilder GetUsageAttributeOrFail()
        {
            var attributeUsageCtor = GetConstructorOrFail(typeof(AttributeUsageAttribute), new[] { typeof(AttributeTargets) });
            var allowMultipleInfo = typeof(AttributeUsageAttribute).GetProperty("AllowMultiple", BindingFlags.Instance | BindingFlags.Public);
            if (attributeUsageCtor == null || allowMultipleInfo == null)
            {
                // must not happen ever
                throw new ResolutionException($"Failed to get AttributeUsageAttribute ctor or AllowMultiple property");
            }

            return new CustomAttributeBuilder(attributeUsageCtor, new object[] { AttributeTargets.Class | AttributeTargets.Method }, new[] { allowMultipleInfo }, new object[] { true });
        }

        private static Type GetTypeOrFail(string typeName)
        {
            var type = Type.GetType(typeName);
            if (type == null)
            {
                throw new ResolutionException($"Failed to get {typeName} type");
            }
            return type;
        }

        private static MethodInfo GetMethodOrFail(Type type, string methodName, Type[] paramTypes = null)
        {
            var method = type.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public, null, paramTypes ?? new Type[0], null);
            if (method == null)
            {
                throw new ResolutionException($"Failed to get {methodName} method from type {type}");
            }
            return method;
        }

        private static ConstructorInfo GetConstructorOrFail(Type type, Type[] paramTypes)
        {
            var ctor = type.GetConstructor(paramTypes);
            if (ctor == null)
            {
                throw new ResolutionException($"Failed to get constructor from type {type}");
            }
            return ctor;
        }

        private static PropertyInfo GetPropertyOrFail(Type type, string propertyName)
        {
            var prop = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
            if (prop == null)
            {
                throw new ResolutionException($"Failed to get {propertyName} property info from type {type}");
            }

            return prop;
        }

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

        class ResolutionException : Exception
        {
            public ResolutionException(string message) : base(message)
            {
            }
        }

        #endregion
    }
}

﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.ServiceModel;
using WcfClientProxyGenerator.Util;

namespace WcfClientProxyGenerator
{
    /// <summary>
    /// Static class to hold the cached instance of the dynamic assembly
    /// </summary>
    internal static class DynamicProxyAssembly
    {
        static DynamicProxyAssembly()
        {
            var assemblyName = new AssemblyName("WcfClientProxyGenerator.DynamicProxy");
            var appDomain = System.Threading.Thread.GetDomain();

#if OUTPUT_PROXY_DLL
            AssemblyBuilder = appDomain.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.RunAndSave);
            ModuleBuilder = AssemblyBuilder.DefineDynamicModule(assemblyName.Name, "WcfClientProxyGenerator.DynamicProxy.dll");
#else
            AssemblyBuilder = appDomain.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.Run);
            ModuleBuilder = AssemblyBuilder.DefineDynamicModule(assemblyName.Name);
#endif
        }

        public static AssemblyBuilder AssemblyBuilder { get; private set; }
        public static ModuleBuilder ModuleBuilder { get; private set; }
    }

    /// <summary>
    /// Dynamic type generator for WCF interfaces. Builds an implementation
    /// of <typeparamref name="TServiceInterface"/> at runtime that passes calls
    /// through to the <see cref="IActionInvoker{TServiceInterface}"/>
    /// </summary>
    /// <typeparam name="TServiceInterface">
    /// WCF based interface that is decorated with the <c>System.ServiceModel</c> attributes
    /// </typeparam>
    internal static class DynamicProxyTypeGenerator<TServiceInterface>
        where TServiceInterface : class
    {
        public static Type GenerateType<TActionInvokerProvider>()
            where TActionInvokerProvider : IActionInvokerProvider<TServiceInterface>
        {
            var moduleBuilder = DynamicProxyAssembly.ModuleBuilder;

            var typeBuilder = moduleBuilder.DefineType(
                "-proxy-" + typeof(TServiceInterface).Name,
                TypeAttributes.Public | TypeAttributes.Class,
                typeof(TActionInvokerProvider));
            
            typeBuilder.AddInterfaceImplementation(typeof(TServiceInterface));

            SetDebuggerDisplay(typeBuilder, typeof(TServiceInterface).Name + " (wcf proxy)");

            var interfaceTypeHierarchy = typeof(TServiceInterface)
                .GetAllInheritedTypes(includeInterfaces: true)
                .Where(t => t.IsInterface);

            var serviceMethods = interfaceTypeHierarchy
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                .Where(t => t.GetCustomAttribute<OperationContractAttribute>() != null);

            foreach (var serviceMethod in serviceMethods)
            {
                GenerateServiceProxyMethod(serviceMethod, typeBuilder);
            }

            Type generatedType = typeBuilder.CreateType();

#if OUTPUT_PROXY_DLL
            DynamicProxyAssembly.AssemblyBuilder.Save("WcfClientProxyGenerator.DynamicProxy.dll");
#endif

            return generatedType;
        }

        private static void SetDebuggerDisplay(TypeBuilder typeBuilder, string display)
        {
            var attributCtor = typeof(DebuggerDisplayAttribute)
                .GetConstructor(new[] { typeof(string) });
           
            if (attributCtor == null)
                throw new NotImplementedException("No constructor found on type 'DebuggerDisplayAttribute' that takes an argument of 'string'");

            var attributeBuilder = new CustomAttributeBuilder(attributCtor, new object[] { display });
            typeBuilder.SetCustomAttribute(attributeBuilder);
        }

        /// <summary>
        /// Generates the methods on the <paramref name="typeBuilder">dynamic type</paramref> 
        /// to satisfy the <see cref="OperationContractAttribute"/> interface contracts.
        /// </summary>
        /// <param name="methodInfo">MethodInfo from the interface</param>
        /// <param name="typeBuilder">The dynamic type</param>
        private static void GenerateServiceProxyMethod(
            MethodInfo methodInfo, 
            TypeBuilder typeBuilder)
        {
            var parameterTypes = methodInfo.GetParameters()
                .Select(m => m.ParameterType)
                .ToArray();

            var methodBuilder = typeBuilder.DefineMethod(
                methodInfo.Name,
                MethodAttributes.Public | MethodAttributes.Final | MethodAttributes.Virtual,
                methodInfo.ReturnType,
                parameterTypes);

            Type serviceCallWrapperType;
            var serviceCallWrapperFields = GenerateServiceCallWrapperType(
                methodInfo, 
                parameterTypes, 
                out serviceCallWrapperType);

            var ilGenerator = methodBuilder.GetILGenerator();
            ilGenerator.DeclareLocal(typeof(RetryingWcfActionInvoker<TServiceInterface>));

            ilGenerator.DeclareLocal(methodInfo.ReturnType == typeof(void)
                ? typeof(Action<>).MakeGenericType(typeof(TServiceInterface))
                : typeof(Func<,>).MakeGenericType(typeof(TServiceInterface), methodInfo.ReturnType));

            ilGenerator.DeclareLocal(serviceCallWrapperType);
            
            if (methodInfo.ReturnType != typeof(void))
                ilGenerator.DeclareLocal(methodInfo.ReturnType);

            var serviceCallWrapperCtor = serviceCallWrapperType.GetConstructor(Type.EmptyTypes);
            if (serviceCallWrapperCtor == null)
                throw new Exception("Parameterless constructor not found for type: " + serviceCallWrapperType);

            ilGenerator.Emit(OpCodes.Newobj, serviceCallWrapperCtor);
            ilGenerator.Emit(OpCodes.Stloc_2);
            ilGenerator.Emit(OpCodes.Ldloc_2);
            
            for (int i = 0; i < serviceCallWrapperFields.Count; i++)
            {
                ilGenerator.Emit(OpCodes.Ldarg, i + 1);
                ilGenerator.Emit(OpCodes.Stfld, serviceCallWrapperType.GetField(serviceCallWrapperFields[i].Name));

                if (i < serviceCallWrapperFields.Count)
                    ilGenerator.Emit(OpCodes.Ldloc_2);
            }

            ilGenerator.Emit(OpCodes.Nop);
            ilGenerator.Emit(OpCodes.Ldarg_0);
            
            var channelProperty = typeof(RetryingWcfActionInvokerProvider<TServiceInterface>)
                .GetMethod(
                    "get_ActionInvoker", 
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.GetProperty);

            ilGenerator.Emit(OpCodes.Call, channelProperty);
            ilGenerator.Emit(OpCodes.Stloc_0);
            ilGenerator.Emit(OpCodes.Ldloc_2);

            var serviceCallWrapperGetMethod = serviceCallWrapperType
                .GetMethod("Get", BindingFlags.Instance | BindingFlags.Public);
            
            ilGenerator.Emit(OpCodes.Ldftn, serviceCallWrapperGetMethod);
            
            ConstructorInfo ctor = GetDelegateConstructor(methodInfo);
            
            ilGenerator.Emit(OpCodes.Newobj, ctor);
            ilGenerator.Emit(OpCodes.Stloc_1);
            ilGenerator.Emit(OpCodes.Ldloc_0);
            ilGenerator.Emit(OpCodes.Ldloc_1);

            MethodInfo invokeMethod = GetIActionInvokerInvokeMethod(methodInfo);

            ilGenerator.Emit(OpCodes.Callvirt, invokeMethod);

            if (methodInfo.ReturnType != typeof(void))
            {
                ilGenerator.Emit(OpCodes.Stloc_3);
                ilGenerator.Emit(OpCodes.Ldloc_3);
            }

            ilGenerator.Emit(OpCodes.Ret);
        }

        private static ConstructorInfo GetDelegateConstructor(MethodInfo methodInfo)
        {
            if (methodInfo.ReturnType == typeof(void))
            {
                return typeof(Action<>)
                    .MakeGenericType(typeof(TServiceInterface))
                    .GetConstructor(new Type[] { typeof(object), typeof(IntPtr) });
            }

            return typeof(Func<,>)
                .MakeGenericType(typeof(TServiceInterface), methodInfo.ReturnType)
                .GetConstructor(new Type[] { typeof(object), typeof(IntPtr) });
        }

        private static MethodInfo GetIActionInvokerInvokeMethod(MethodInfo methodInfo)
        {
            if (methodInfo.ReturnType == typeof(void))
            {
                Type actionType = typeof(Action<>)
                    .MakeGenericType(typeof(TServiceInterface));

                return typeof(IActionInvoker<TServiceInterface>)
                    .GetMethod("Invoke", new[] { actionType });
            }

            var funcInvokeMethod = typeof(IActionInvoker<TServiceInterface>)
                .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .First(m => m.Name == "Invoke" && m.ReturnType != typeof(void));

            return funcInvokeMethod.MakeGenericMethod(new[] { methodInfo.ReturnType });
        }

        /// <summary>
        /// Builds the type used by the call to the <see cref="IActionInvoker{TServiceInterface}.Invoke{TResponse}"/>
        /// method.
        /// </summary>
        private static IList<FieldBuilder> GenerateServiceCallWrapperType(
            MethodInfo methodInfo, 
            Type[] parameterTypes, 
            out Type generatedType)
        {
            string typeName = string.Format(
                "-call-{0}.{1}",
                typeof(TServiceInterface).Name,
                methodInfo.Name);

            var serviceCallTypeBuilder = DynamicProxyAssembly.ModuleBuilder.DefineType(typeName);

            var fields = new List<FieldBuilder>(parameterTypes.Length);
            for (int i = 0; i < parameterTypes.Length; i++)
            {
                Type parameterType = parameterTypes[i];
                fields.Add(
                    serviceCallTypeBuilder.DefineField("arg" + i, parameterType, FieldAttributes.Public));
            }

            var methodBuilder = serviceCallTypeBuilder.DefineMethod(
                "Get",
                MethodAttributes.Public,
                methodInfo.ReturnType,
                new[] { typeof(TServiceInterface) });

            var ilGenerator = methodBuilder.GetILGenerator();
            
            if (methodInfo.ReturnType != typeof(void))
                ilGenerator.DeclareLocal(methodInfo.ReturnType);
            
            ilGenerator.Emit(OpCodes.Ldarg_1);

            fields.ForEach(lf =>
            {
                ilGenerator.Emit(OpCodes.Ldarg_0);
                ilGenerator.Emit(OpCodes.Ldfld, lf);
            });

            ilGenerator.Emit(OpCodes.Callvirt, methodInfo);
            
            if (methodInfo.ReturnType != typeof(void))
            {
                ilGenerator.Emit(OpCodes.Stloc_0);
                ilGenerator.Emit(OpCodes.Ldloc_0);
            }

            ilGenerator.Emit(OpCodes.Ret);

            generatedType = serviceCallTypeBuilder.CreateType();
            return fields;
        }
    }
}
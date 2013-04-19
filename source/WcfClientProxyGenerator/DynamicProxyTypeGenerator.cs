﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.ServiceModel;

namespace WcfClientProxyGenerator
{
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

    internal static class DynamicProxyTypeGenerator<TServiceInterface>
        where TServiceInterface : class
    {
        public static Type GenerateType()
        {
            var moduleBuilder = DynamicProxyAssembly.ModuleBuilder;

            var typeBuilder = moduleBuilder.DefineType(
                "-proxy-" + typeof(TServiceInterface).Name,
                TypeAttributes.Public | TypeAttributes.Class,
                typeof(RetryingWcfActionInvokerProvider<TServiceInterface>));
            
            typeBuilder.AddInterfaceImplementation(typeof(TServiceInterface));

            SetDebuggerDisplay(typeBuilder, typeof(TServiceInterface).Name + " (wcf proxy)");

//            GenerateTypeConstructor(typeBuilder, typeof(string));
//            GenerateTypeConstructor(typeBuilder, typeof(Binding), typeof(EndpointAddress));

            var serviceMethods = typeof(TServiceInterface)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(t => t.GetCustomAttribute<OperationContractAttribute>() != null);

            foreach (var serviceMethod in serviceMethods)
            {
                GenerateServiceProxyMethod(serviceMethod, moduleBuilder, typeBuilder);
            }

            Type generatedType = typeBuilder.CreateType();

#if OUTPUT_PROXY_DLL
            DynamicProxyAssembly.AssemblyBuilder.Save("WcfClientProxyGenerator.DynamicProxy.dll");
#endif

            return generatedType;
        }

        private static void SetDebuggerDisplay(TypeBuilder typeBuilder, string display)
        {
            var attributCtor = typeof(DebuggerDisplayAttribute).GetConstructor(new[] { typeof(string) });
            var attributeBuilder = new CustomAttributeBuilder(attributCtor, new object[] { display });
            typeBuilder.SetCustomAttribute(attributeBuilder);
        }

        private static void GenerateTypeConstructor(TypeBuilder typeBuilder, params Type[] argumentParameterTypes)
        {
            var constructorBuilder = typeBuilder.DefineConstructor(
                MethodAttributes.Public, 
                CallingConventions.Standard, 
                argumentParameterTypes);

            var ilGenerator = constructorBuilder.GetILGenerator();

            ilGenerator.Emit(OpCodes.Ldarg_0); // this

            for (int i = 0; i < argumentParameterTypes.Length; i++)
                ilGenerator.Emit(OpCodes.Ldarg, (i + 1));

            var baseCtor = typeof(RetryingWcfActionInvokerProvider<TServiceInterface>)
                .GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, argumentParameterTypes, null);

            ilGenerator.Emit(OpCodes.Call, baseCtor);
            ilGenerator.Emit(OpCodes.Ret);
        }

        private static void GenerateServiceProxyMethod(MethodInfo methodInfo, ModuleBuilder moduleBuilder, TypeBuilder typeBuilder)
        {
            var parameterTypes = methodInfo.GetParameters().Select(m => m.ParameterType).ToArray();

            var methodBuilder = typeBuilder.DefineMethod(
                methodInfo.Name,
                MethodAttributes.Public | MethodAttributes.Final | MethodAttributes.Virtual,
                methodInfo.ReturnType,
                parameterTypes);

            Type actionInvokerLambdaType;
            var actionInvokerLambdaFields = GenerateActionInvokerLambdaType(methodInfo, moduleBuilder, parameterTypes, out actionInvokerLambdaType);

            var ilGenerator = methodBuilder.GetILGenerator();
            ilGenerator.DeclareLocal(typeof(RetryingWcfActionInvoker<TServiceInterface>));

            ilGenerator.DeclareLocal(methodInfo.ReturnType == typeof(void)
                ? typeof(Action<>).MakeGenericType(typeof(TServiceInterface))
                : typeof(Func<,>).MakeGenericType(typeof(TServiceInterface), methodInfo.ReturnType));

            ilGenerator.DeclareLocal(actionInvokerLambdaType);
            
            if (methodInfo.ReturnType != typeof(void))
                ilGenerator.DeclareLocal(methodInfo.ReturnType);

            var lambdaCtor = actionInvokerLambdaType.GetConstructor(Type.EmptyTypes);

            ilGenerator.Emit(OpCodes.Newobj, lambdaCtor);
            ilGenerator.Emit(OpCodes.Stloc_2);
            ilGenerator.Emit(OpCodes.Ldloc_2);
            
            for (int i = 0; i < actionInvokerLambdaFields.Count; i++)
            {
                ilGenerator.Emit(OpCodes.Ldarg, i + 1);
                ilGenerator.Emit(OpCodes.Stfld, actionInvokerLambdaType.GetField(actionInvokerLambdaFields[i].Name));

                if (i < actionInvokerLambdaFields.Count)
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

            var lambdaGetMethod = actionInvokerLambdaType.GetMethod("Get", BindingFlags.Instance | BindingFlags.Public);
            ilGenerator.Emit(OpCodes.Ldftn, lambdaGetMethod);
            
            // new func<TService, TReturn>
            ConstructorInfo ctor = null;
            if (methodInfo.ReturnType != typeof(void))
            {
                ctor = typeof(Func<,>)
                    .MakeGenericType(typeof(TServiceInterface), methodInfo.ReturnType)
                    .GetConstructor(new Type[] { typeof(object), typeof(IntPtr) });
            }
            else
            {
                ctor = typeof(Action<>)
                    .MakeGenericType(typeof(TServiceInterface))
                    .GetConstructor(new Type[] { typeof(object), typeof(IntPtr) });
            }
            
            ilGenerator.Emit(OpCodes.Newobj, ctor);
            ilGenerator.Emit(OpCodes.Stloc_1);
            ilGenerator.Emit(OpCodes.Ldloc_0);
            ilGenerator.Emit(OpCodes.Ldloc_1);

            MethodInfo invokeMethod = null;
            if (methodInfo.ReturnType != typeof(void))
            {
                invokeMethod = typeof(RetryingWcfActionInvoker<TServiceInterface>)
                    .GetMethod("Invoke", BindingFlags.Instance | BindingFlags.Public)
                    .MakeGenericMethod(new[] { methodInfo.ReturnType });
            }
            else
            {
                invokeMethod = typeof(RetryingWcfActionInvoker<TServiceInterface>)
                    .GetMethod("InvokeAction", BindingFlags.Instance | BindingFlags.Public);
            }

            ilGenerator.Emit(OpCodes.Callvirt, invokeMethod);

            if (methodInfo.ReturnType != typeof(void))
            {
                ilGenerator.Emit(OpCodes.Stloc_3);
                ilGenerator.Emit(OpCodes.Ldloc_3);
            }

            ilGenerator.Emit(OpCodes.Ret);
        }

        /// <summary>
        /// Builds the type used by the call to the <see cref="IActionInvoker{TServiceInterface}.Invoke{TResponse}"/>
        /// method.
        /// </summary>
        /// <param name="methodInfo"></param>
        /// <param name="moduleBuilder"></param>
        /// <param name="parameterTypes"></param>
        /// <param name="lambdaType"></param>
        /// <returns></returns>
        private static IList<FieldBuilder> GenerateActionInvokerLambdaType(MethodInfo methodInfo, ModuleBuilder moduleBuilder, Type[] parameterTypes, out Type lambdaType)
        {
            string typeName = string.Format("-lambda-{0}.{1}", methodInfo.DeclaringType.Name, methodInfo.Name);
            var lambdaTypeBuilder = moduleBuilder.DefineType(typeName);

            var lambdaFields = new List<FieldBuilder>(parameterTypes.Length);
            for (int i = 0; i < parameterTypes.Length; i++)
            {
                Type parameterType = parameterTypes[i];
                lambdaFields.Add(
                    lambdaTypeBuilder.DefineField("arg" + i, parameterType, FieldAttributes.Public));
            }

            var lambdaMethodBuilder = lambdaTypeBuilder.DefineMethod(
                "Get",
                MethodAttributes.Public,
                methodInfo.ReturnType,
                new[] { typeof(TServiceInterface) });

            var lambdaIlGenerator = lambdaMethodBuilder.GetILGenerator();
            
            if (methodInfo.ReturnType != typeof(void))
                lambdaIlGenerator.DeclareLocal(methodInfo.ReturnType);
            
            lambdaIlGenerator.Emit(OpCodes.Ldarg_1);

            lambdaFields.ForEach(lf =>
            {
                lambdaIlGenerator.Emit(OpCodes.Ldarg_0);
                lambdaIlGenerator.Emit(OpCodes.Ldfld, lf);
            });

            lambdaIlGenerator.Emit(OpCodes.Callvirt, methodInfo);
            
            if (methodInfo.ReturnType != typeof(void))
            {
                lambdaIlGenerator.Emit(OpCodes.Stloc_0);
                lambdaIlGenerator.Emit(OpCodes.Ldloc_0);
            }

            lambdaIlGenerator.Emit(OpCodes.Ret);

            lambdaType = lambdaTypeBuilder.CreateType();
            return lambdaFields;
        }
    }
}

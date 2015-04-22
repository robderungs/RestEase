﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Net.Http;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RestEase
{
    public class ImplementationBuilder
    {
        private static readonly string factoryAssemblyName = "RestEaseAutoGeneratedAssembly";
        private static readonly string moduleBuilderName = "RestEaseAutoGeneratedModule";

        private static readonly MethodInfo requestVoidAsyncMethod = typeof(IRequester).GetMethod("RequestVoidAsync");
        private static readonly MethodInfo requestAsyncMethod = typeof(IRequester).GetMethod("RequestAsync");
        private static readonly ConstructorInfo requestInfoCtor = typeof(RequestInfo).GetConstructor(new[] { typeof(HttpMethod), typeof(string), typeof(CancellationToken) });
        private static readonly MethodInfo cancellationTokenNoneGetter = typeof(CancellationToken).GetProperty("None").GetMethod;
        private static readonly MethodInfo addParameterMethod = typeof(RequestInfo).GetMethod("AddParameter");
        private static readonly MethodInfo toStringMethod = typeof(Object).GetMethod("ToString");

        private static readonly Dictionary<HttpMethod, PropertyInfo> httpMethodProperties = new Dictionary<HttpMethod, PropertyInfo>()
        {
            { HttpMethod.Delete, typeof(HttpMethod).GetProperty("Delete") },
            { HttpMethod.Get, typeof(HttpMethod).GetProperty("Get") },
            { HttpMethod.Head, typeof(HttpMethod).GetProperty("Head") },
            { HttpMethod.Options, typeof(HttpMethod).GetProperty("Options") },
            { HttpMethod.Post, typeof(HttpMethod).GetProperty("Post") },
            { HttpMethod.Put, typeof(HttpMethod).GetProperty("Put") },
            { HttpMethod.Trace, typeof(HttpMethod).GetProperty("Trace") }
        };

        private readonly ModuleBuilder moduleBuilder;
        private readonly ConcurrentDictionary<Type, Func<IRequester, object>> creatorCache = new ConcurrentDictionary<Type, Func<IRequester, object>>();

        public ImplementationBuilder()
        {
            var assemblyName = new AssemblyName(factoryAssemblyName);
            var assemblyBuilder = AppDomain.CurrentDomain.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.Run);
            var moduleBuilder = assemblyBuilder.DefineDynamicModule(moduleBuilderName);
            this.moduleBuilder = moduleBuilder;
        }

        public T CreateImplementation<T>(IRequester requester)
        {
            var creator = this.creatorCache.GetOrAdd(typeof(T), key =>
            {
                var implementationType = this.BuildImplementationImpl(key);
                return this.BuildCreator(implementationType);
            });

            T implementation = (T)creator(requester);
            return implementation;
        }

        public Func<IRequester, object> BuildCreator(Type implementationType)
        {
            var requesterParam = Expression.Parameter(typeof(IRequester));
            var ctor = Expression.New(implementationType.GetConstructor(new[] { typeof(IRequester) }), requesterParam);
            return Expression.Lambda<Func<IRequester, object>>(ctor, requesterParam).Compile();
        }

        private Type BuildImplementationImpl(Type interfaceType)
        {
            if (!interfaceType.IsInterface)
                throw new ArgumentException(String.Format("Type {0} is not an interface", interfaceType.Name));

            var typeBuilder = this.moduleBuilder.DefineType(String.Format("RestEase.AutoGenerated.{0}", interfaceType.FullName), TypeAttributes.Public);
            typeBuilder.AddInterfaceImplementation(interfaceType);

            // Define a field which holds a reference to the IRequester
            var requesterField = typeBuilder.DefineField("requester", typeof(IRequester), FieldAttributes.Private);

            // Add a constructor which takes the IRequester and assigns it to the field
            // public Name(IRequester requester)
            // {
            //     this.requester = requester;
            // }
            var ctorBuilder = typeBuilder.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, new[] { typeof(IRequester) });
            var ctorIlGenerator = ctorBuilder.GetILGenerator();
            // Load 'this' and the requester onto the stack
            ctorIlGenerator.Emit(OpCodes.Ldarg_0);
            ctorIlGenerator.Emit(OpCodes.Ldarg_1);
            // Store the requester into this.requester
            ctorIlGenerator.Emit(OpCodes.Stfld, requesterField);
            ctorIlGenerator.Emit(OpCodes.Ret);

            foreach (var methodInfo in interfaceType.GetMethods())
            {
                var requestAttribute = methodInfo.GetCustomAttribute<RequestAttribute>();
                if (requestAttribute == null)
                    throw new RestEaseImplementationCreationException(String.Format("Method {0} does not have a suitable attribute on it", methodInfo.Name));

                var parameters = methodInfo.GetParameters();
                var indexedParameters = parameters.Select((x, i) => new { Index = i, Parameter = x }).ToArray();

                var cancellationTokenParameters = indexedParameters.Where(x => x.Parameter.ParameterType == typeof(CancellationToken)).ToArray();
                if (cancellationTokenParameters.Length > 1)
                    throw new RestEaseImplementationCreationException(String.Format("Found more than one parameter of type CancellationToken for method {0}", methodInfo.Name));
                int? cancellationTokenParameterIndex = cancellationTokenParameters.Length > 0 ? (int?)cancellationTokenParameters[0].Index : null;

                var methodBuilder = typeBuilder.DefineMethod(methodInfo.Name, MethodAttributes.Public | MethodAttributes.Virtual, methodInfo.ReturnType, parameters.Select(x => x.ParameterType).ToArray());
                var methodIlGenerator = methodBuilder.GetILGenerator();

                // Load 'this' onto the stack
                // Stack: [this]
                methodIlGenerator.Emit(OpCodes.Ldarg_0);
                // Load 'this.requester' onto the stack
                // Stack: [this.requester]
                methodIlGenerator.Emit(OpCodes.Ldfld, requesterField);

                // Start loading the ctor params for RequestInfo onto the stack
                // 1. HttpMethod
                // Stack: [this.requester, HttpMethod]
                methodIlGenerator.Emit(OpCodes.Call, httpMethodProperties[requestAttribute.Method].GetMethod);
                // 2. The Path
                // Stack: [this.requester, HttpMethod, path]
                methodIlGenerator.Emit(OpCodes.Ldstr, requestAttribute.Path);
                // 3. The CancellationToken
                // Stack: [this.requester, HttpMethod, path, cancellationToken]
                if (cancellationTokenParameterIndex.HasValue)
                    methodIlGenerator.Emit(OpCodes.Ldarg, (short)cancellationTokenParameterIndex.Value);
                else
                    methodIlGenerator.Emit(OpCodes.Call, cancellationTokenNoneGetter);

                // Ctor the RequestInfo
                // Stack: [this.requester, requestInfo]
                methodIlGenerator.Emit(OpCodes.Newobj, requestInfoCtor);

                foreach (var parameter in indexedParameters)
                {
                    if (cancellationTokenParameterIndex.HasValue && cancellationTokenParameterIndex.Value == parameter.Index)
                        continue;

                    // For the moment, only look at those with a QueryParamAttribute
                    var queryParamAttribute = parameter.Parameter.GetCustomAttribute<QueryParamAttribute>();
                    if (queryParamAttribute != null)
                    {
                        // Equivalent C#:
                        // requestInfo.AddParameter("name", value.ToString());

                        // Duplicate the requestInfo. This is because calling AddParameter on it will pop it
                        // Stack: [..., requestInfo, requestInfo]
                        methodIlGenerator.Emit(OpCodes.Dup);
                        // Load the name onto the stack
                        // Stack: [..., requestInfo, requestInfo, name]
                        methodIlGenerator.Emit(OpCodes.Ldstr, queryParamAttribute.Name);
                        // Load the param onto the stack
                        // Stack: [..., requestInfo, requestInfo, name, value]
                        methodIlGenerator.Emit(OpCodes.Ldarg, (short)parameter.Index);
                        // Call ToString on the value
                        // Stack: [..., requestInfo, requestInfo, name, valueAsString]
                        methodIlGenerator.Emit(OpCodes.Callvirt, toStringMethod);
                        // Call AddParameter
                        // Stack: [..., requestInfo]
                        methodIlGenerator.Emit(OpCodes.Callvirt, addParameterMethod);
                    }
                }

                // Call the appropriate RequestAsync method, depending on whether or not we have a return type
                if (methodInfo.ReturnType == typeof(Task))
                {
                    // Stack: [Task]
                    methodIlGenerator.Emit(OpCodes.Callvirt, requestVoidAsyncMethod);
                }
                else if (methodInfo.ReturnType.IsGenericType && methodInfo.ReturnType.GetGenericTypeDefinition() == typeof(Task<>))
                {
                    // Stack: [Task<deserializedResponse>]
                    var typeOfT = methodInfo.ReturnType.GetGenericArguments()[0];
                    var typedRequestAsyncMethod = requestAsyncMethod.MakeGenericMethod(typeOfT);
                    methodIlGenerator.Emit(OpCodes.Callvirt, typedRequestAsyncMethod);
                }
                else
                {
                    throw new RestEaseImplementationCreationException(String.Format("Method {0} has a return type that is not Task<T> or Task", methodInfo.Name));
                }

                // Finally, return
                methodIlGenerator.Emit(OpCodes.Ret);

                typeBuilder.DefineMethodOverride(methodBuilder, methodInfo);
            }

            Type constructedType;
            try
            {
                constructedType = typeBuilder.CreateType();
            }
            catch (TypeLoadException e)
            {
                var msg = String.Format("Unable to create implementation for interface {0}. . Ensure that the interface is public", interfaceType.FullName);
                throw new RestEaseImplementationCreationException(msg, e);
            }

            return constructedType;
        }
    }
}


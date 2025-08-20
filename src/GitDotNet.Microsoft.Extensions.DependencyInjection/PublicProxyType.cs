using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Emit;

namespace GitDotNet.Microsoft.Extensions.DependencyInjection;
/// <summary>
/// Provides functionality to create and retrieve proxy types with public constructors to invoke
/// internal constructors of non-sealed base classes.
/// </summary>
/// <remarks>This class is designed to generate proxy types that expose internal constructors of
/// non-sealed classes as public constructors. Proxy types are cached for performance, ensuring that repeated calls
/// to retrieve a proxy type for the same base type are efficient.</remarks>
internal static class PublicProxyType
{
    private static readonly ConcurrentDictionary<Type, Type> _cache = new();
    private static readonly AssemblyName _proxyAssemblyName = new("GitDotNet.DynamicProxies");
    private static readonly ModuleBuilder _moduleBuilder = AssemblyBuilder
        .DefineDynamicAssembly(_proxyAssemblyName, AssemblyBuilderAccess.Run)
        .DefineDynamicModule(_proxyAssemblyName.Name!);

    public static Type Get(Type type) => _cache.GetOrAdd(type, CreateProxyType);

    private static Type CreateProxyType(Type type)
    {
        var baseConstructor = FindInternalConstructor(type);
        var parameters = baseConstructor.GetParameters();
        var paramTypes = parameters.Select(p => p.ParameterType).ToArray();

        var proxyType = DefineProxyTypeBuilder(type);
        var constructor = DefinePublicConstructor(parameters, paramTypes, proxyType);
        CallBaseConstructor(baseConstructor, paramTypes, constructor.GetILGenerator());

        return proxyType.CreateType() ?? throw new InvalidOperationException("Failed to create proxy type.");
    }

    private static ConstructorInfo FindInternalConstructor(Type type)
    {
        if (type.IsSealed)
            throw new InvalidOperationException($"Cannot proxy sealed type {type.FullName}.");
        if (type.IsGenericTypeDefinition)
            throw new NotSupportedException("Open generics not supported in this simple proxy.");

        // Pick first internal (non-public, non-private, non-protected) instance ctor
        var baseConstructor = type
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .OrderByDescending(c => c.GetParameters().Length)
            .FirstOrDefault(c => c.IsAssembly) ??
            throw new InvalidOperationException($"No internal constructor found on {type.FullName}.");
        return baseConstructor;
    }

    private static TypeBuilder DefineProxyTypeBuilder(Type type) =>
        _moduleBuilder.DefineType(
            $"{type.Namespace}.Generated.{type.Name}CtorProxy",
            TypeAttributes.Public | TypeAttributes.Class,
            type);

    private static ConstructorBuilder DefinePublicConstructor(ParameterInfo[] baseParams, Type[] paramTypes, TypeBuilder tb)
    {
        // Define public ctor with SAME signature as internal base ctor.
        var result = tb.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, paramTypes);

        for (int i = 0; i < baseParams.Length; i++)
        {
            // Preserve parameter names (helps debugging / DI exception messages)
            var parameter = result.DefineParameter(i + 1, ParameterAttributes.None, baseParams[i].Name);

            // Preserve parameter attributes (e.g., [Optional], [DefaultValue])
            if (baseParams[i].HasDefaultValue)
                parameter.SetConstant(baseParams[i].DefaultValue);
        }

        return result;
    }

    private static void CallBaseConstructor(ConstructorInfo baseConstructor, Type[] paramTypes, ILGenerator il)
    {
        // this
        il.Emit(OpCodes.Ldarg_0);
        // Push all original parameters
        for (short i = 0; i < paramTypes.Length; i++)
        {
            EmitParameterLoad(il, i);
        }
        il.Emit(OpCodes.Call, baseConstructor);
        il.Emit(OpCodes.Ret);
    }

    private static void EmitParameterLoad(ILGenerator il, short i)
    {
        // ldarg.1 .. ldarg.s pattern
        switch (i + 1)
        {
            case 1:
                il.Emit(OpCodes.Ldarg_1);
                break;
            case 2:
                il.Emit(OpCodes.Ldarg_2);
                break;
            case 3:
                il.Emit(OpCodes.Ldarg_3);
                break;
            default:
                il.Emit(OpCodes.Ldarg_S, (byte)(i + 1));
                break;
        }
    }
}
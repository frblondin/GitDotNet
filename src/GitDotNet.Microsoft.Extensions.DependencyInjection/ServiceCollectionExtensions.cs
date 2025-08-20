using System.Linq.Expressions;
using System.Reflection;
using GitDotNet.Microsoft.Extensions.DependencyInjection;

namespace Microsoft.Extensions.DependencyInjection;
internal static class ServiceCollectionExtensions
{
    private static readonly MethodInfo? _createInstanceThroughActivatorMethod =
        typeof(ActivatorUtilities)
        .GetMethod("CreateInstance", [typeof(IServiceProvider), typeof(Type), typeof(object[])]);

    public static IServiceCollection AddAutoFactory<TFactory>(this IServiceCollection services, ServiceLifetime lifetime)
        where TFactory : class =>
        services.AddAutoFactory<TFactory>(typeof(TFactory).GetMethod("Invoke")!.ReturnType, lifetime);

    public static IServiceCollection AddAutoFactory<TFactory, TConcreteType>(this IServiceCollection services, ServiceLifetime lifetime)
        where TFactory : class =>
        services.AddAutoFactory<TFactory>(typeof(TConcreteType), lifetime);

    public static IServiceCollection AddAutoFactory<TFactory>(this IServiceCollection services, Type concreteType, ServiceLifetime lifetime)
        where TFactory : class
    {
        var type = GetTypeWithPublicConstructor(services, concreteType);
        var serviceType = typeof(TFactory).GetMethod("Invoke")!.ReturnType;
        services.Add(new ServiceDescriptor(serviceType, type, lifetime));
        services.AddSingleton(sp => CreateFactory<TFactory>(sp, type));
        return services;
    }

    private static Type GetTypeWithPublicConstructor(IServiceCollection services, Type type) =>
        type.GetConstructors(BindingFlags.Instance | BindingFlags.Public).Any(c => c.IsPublic) ?
        type :
        PublicProxyType.Get(type);


    private static TFactory CreateFactory<TFactory>(IServiceProvider serviceProvider, Type concreteType)
        where TFactory : class
    {
        var method = typeof(TFactory).GetMethod("Invoke")!;
        var parameters = method.GetParameters().Select(p => Expression.Parameter(p.ParameterType, p.Name)).ToList();
        var lambda = Expression.Lambda<TFactory>(
            Expression.Convert(
                Expression.Call(_createInstanceThroughActivatorMethod!,
                    Expression.Constant(serviceProvider),
                    Expression.Constant(concreteType),
                    Expression.NewArrayInit(typeof(object), parameters.Select(p => Expression.Convert(p, typeof(object))))),
                method.ReturnType),
            parameters);
        return lambda.Compile();
    }
}
using System.Linq.Expressions;
using System.Reflection;

namespace MiniNet.DependencyInjection;

/// <summary>Walks the call site graph on every resolution, calling constructors through reflection.</summary>
internal static class ReflectionResolver
{
    public static object? Resolve(CallSite site, ServiceProviderEngineScope scope) => site switch
    {
        ConstantCallSite constant => constant.Value,
        ServiceProviderCallSite => scope,
        _ when site.Lifetime is ServiceLifetime.Singleton or ServiceLifetime.Scoped => scope.GetOrCreate(site, s => Create(site, s)),
        _ => scope.CaptureTransient(Create(site, scope)),
    };

    private static object? Create(CallSite site, ServiceProviderEngineScope scope)
    {
        switch (site)
        {
            case ConstructorCallSite constructor:
                var arguments = new object?[constructor.Parameters.Length];
                for (var i = 0; i < arguments.Length; i++) arguments[i] = Resolve(constructor.Parameters[i], scope);
                // DoNotWrapExceptions: a constructor that throws surfaces its own exception, not TargetInvocationException.
                return constructor.Constructor.Invoke(BindingFlags.DoNotWrapExceptions, binder: null, arguments, culture: null);

            case FactoryCallSite factory:
                return factory.Factory(scope);

            case EnumerableCallSite enumerable:
                var array = Array.CreateInstance(enumerable.ItemType, enumerable.Items.Length);
                for (var i = 0; i < enumerable.Items.Length; i++) array.SetValue(Resolve(enumerable.Items[i], scope), i);
                return array;

            default:
                throw new NotSupportedException(site.GetType().Name);
        }
    }
}

/// <summary>
/// Compiles a call site graph into a delegate once, using expression trees. For a transient
/// <c>OrderService(IClock, IRepository)</c> the result is roughly:
/// <code>scope =&gt; scope.CaptureTransient(new OrderService(clockSingleton, (IRepository)scope.GetOrCreate(repoSite, createRepo)))</code>
/// After that, resolving costs a delegate call instead of reflection lookups and an object[] per constructor.
/// </summary>
internal static class CompiledResolver
{
    private static readonly MethodInfo GetOrCreateMethod =
        typeof(ServiceProviderEngineScope).GetMethod(nameof(ServiceProviderEngineScope.GetOrCreate), BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly MethodInfo CaptureTransientMethod =
        typeof(ServiceProviderEngineScope).GetMethod(nameof(ServiceProviderEngineScope.CaptureTransient), BindingFlags.Instance | BindingFlags.NonPublic)!;

    public static Func<ServiceProviderEngineScope, object?> Compile(CallSite site)
    {
        var scope = Expression.Parameter(typeof(ServiceProviderEngineScope), "scope");
        var body = Expression.Convert(Build(site, scope), typeof(object));
        return Expression.Lambda<Func<ServiceProviderEngineScope, object?>>(body, scope).Compile();
    }

    private static Expression Build(CallSite site, ParameterExpression scope)
    {
        switch (site)
        {
            case ConstantCallSite constant:
                return Expression.Constant(constant.Value, site.ServiceType);

            case ServiceProviderCallSite:
                return scope;
        }

        if (site.Lifetime is ServiceLifetime.Singleton or ServiceLifetime.Scoped)
        {
            // Cached services are created by their own compiled delegate, called only on the first resolution in a scope.
            var create = CompileCreate(site);
            var call = Expression.Call(scope, GetOrCreateMethod, Expression.Constant(site, typeof(CallSite)), Expression.Constant(create));
            return Expression.Convert(call, site.ServiceType);
        }

        var transient = Expression.Call(scope, CaptureTransientMethod, Expression.Convert(Create(site, scope), typeof(object)));
        return Expression.Convert(transient, site.ServiceType);
    }

    private static Func<ServiceProviderEngineScope, object?> CompileCreate(CallSite site)
    {
        var scope = Expression.Parameter(typeof(ServiceProviderEngineScope), "scope");
        var body = Expression.Convert(Create(site, scope), typeof(object));
        return Expression.Lambda<Func<ServiceProviderEngineScope, object?>>(body, scope).Compile();
    }

    private static Expression Create(CallSite site, ParameterExpression scope) => site switch
    {
        ConstructorCallSite constructor => Expression.New(
            constructor.Constructor,
            constructor.Parameters.Select((p, i) =>
                (Expression)Expression.Convert(Build(p, scope), constructor.Constructor.GetParameters()[i].ParameterType))),

        FactoryCallSite factory => Expression.Invoke(Expression.Constant(factory.Factory), scope),

        EnumerableCallSite enumerable => Expression.NewArrayInit(
            enumerable.ItemType,
            enumerable.Items.Select(item => (Expression)Expression.Convert(Build(item, scope), enumerable.ItemType))),

        _ => throw new NotSupportedException(site.GetType().Name),
    };
}

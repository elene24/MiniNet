using System.Reflection;

namespace MiniNet.DependencyInjection;

/// <summary>
/// A "call site" is a recipe for building one service: which constructor to call and the recipes
/// for each of its arguments. The container works it out once per service type, validates it
/// (missing, circular and captive dependencies), and then either walks it with reflection or
/// compiles it into a delegate.
/// </summary>
internal abstract class CallSite(Type serviceType, ServiceLifetime lifetime)
{
    public Type ServiceType { get; } = serviceType;

    public ServiceLifetime Lifetime { get; } = lifetime;

    /// <summary>Cached results are stored under this key. Two call sites built from the same registration share it,
    /// so a singleton is the same instance whether resolved as IFoo or as part of IEnumerable&lt;IFoo&gt;.</summary>
    public object CacheKey { get; init; } = new();

    public abstract IEnumerable<CallSite> Dependencies { get; }

    public string Describe() => TypeNames.Format(ServiceType);
}

internal sealed class ConstructorCallSite(Type serviceType, ServiceLifetime lifetime, ConstructorInfo constructor, CallSite[] parameters)
    : CallSite(serviceType, lifetime)
{
    public ConstructorInfo Constructor { get; } = constructor;

    public CallSite[] Parameters { get; } = parameters;

    public override IEnumerable<CallSite> Dependencies => Parameters;
}

internal sealed class FactoryCallSite(Type serviceType, ServiceLifetime lifetime, Func<IServiceProvider, object> factory)
    : CallSite(serviceType, lifetime)
{
    public Func<IServiceProvider, object> Factory { get; } = factory;

    public override IEnumerable<CallSite> Dependencies => [];
}

/// <summary>A pre-built value: a singleton instance, or the default value of an optional constructor parameter.</summary>
internal sealed class ConstantCallSite(Type serviceType, object? value)
    : CallSite(serviceType, ServiceLifetime.Singleton)
{
    public object? Value { get; } = value;

    public override IEnumerable<CallSite> Dependencies => [];
}

internal sealed class EnumerableCallSite(Type itemType, CallSite[] items)
    : CallSite(typeof(IEnumerable<>).MakeGenericType(itemType), ServiceLifetime.Transient)
{
    public Type ItemType { get; } = itemType;

    public CallSite[] Items { get; } = items;

    public override IEnumerable<CallSite> Dependencies => Items;
}

/// <summary>IServiceProvider itself: resolves to whichever scope is doing the resolving.</summary>
internal sealed class ServiceProviderCallSite()
    : CallSite(typeof(IServiceProvider), ServiceLifetime.Scoped)
{
    public override IEnumerable<CallSite> Dependencies => [];
}

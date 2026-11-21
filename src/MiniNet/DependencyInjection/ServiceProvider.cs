using System.Collections.Concurrent;

namespace MiniNet.DependencyInjection;

/// <summary>
/// The root container. Singletons live here; scopes created with <see cref="CreateScope"/> hold scoped services.
/// </summary>
public sealed class ServiceProvider : IServiceProvider, IServiceScopeFactory, IServiceProviderIsService, IDisposable, IAsyncDisposable
{
    private readonly ServiceProviderOptions _options;
    private readonly CallSiteFactory _callSites;
    private readonly ConcurrentDictionary<Type, Func<ServiceProviderEngineScope, object?>> _resolvers = new();

    internal ServiceProvider(IEnumerable<ServiceDescriptor> descriptors, ServiceProviderOptions options)
    {
        _options = options;
        _callSites = new CallSiteFactory(descriptors.ToList());
        _callSites.AddBuiltIn(typeof(IServiceProvider), new ServiceProviderCallSite());
        _callSites.AddBuiltIn(typeof(IServiceScopeFactory), new ConstantCallSite(typeof(IServiceScopeFactory), this));
        _callSites.AddBuiltIn(typeof(IServiceProviderIsService), new ConstantCallSite(typeof(IServiceProviderIsService), this));
        RootScope = new ServiceProviderEngineScope(this, isRoot: true);

        if (options.ValidateOnBuild) ValidateAll();
    }

    internal ServiceProviderEngineScope RootScope { get; }

    public object? GetService(Type serviceType) => Resolve(serviceType, RootScope);

    public IServiceScope CreateScope() => new ServiceProviderEngineScope(this, isRoot: false);

    public bool IsService(Type serviceType) => _callSites.IsRegistered(serviceType);

    public void Dispose() => RootScope.Dispose();

    public ValueTask DisposeAsync() => RootScope.DisposeAsync();

    internal object? Resolve(Type serviceType, ServiceProviderEngineScope scope)
    {
        scope.ThrowIfDisposed();
        return _resolvers.GetOrAdd(serviceType, CreateResolver)(scope);
    }

    private Func<ServiceProviderEngineScope, object?> CreateResolver(Type serviceType)
    {
        var site = _callSites.GetCallSite(serviceType);
        if (site is null) return _ => null;

        if (_options.ValidateScopes) LifetimeValidator.ThrowIfCaptive(site);

        var resolve = _options.Mode == ResolutionMode.Compiled
            ? CompiledResolver.Compile(site)
            : scope => ReflectionResolver.Resolve(site, scope);

        if (_options.ValidateScopes && LifetimeValidator.DependsOnScoped(site))
        {
            var inner = resolve;
            resolve = scope => scope.IsRoot
                ? throw new InvalidOperationException(
                    $"Cannot resolve '{site.Describe()}' from the root provider because it is, or depends on, a scoped service. Create a scope first.")
                : inner(scope);
        }

        return resolve;
    }

    /// <summary>Builds and checks every registration up front, so configuration mistakes fail at startup.</summary>
    private void ValidateAll()
    {
        foreach (var descriptor in _callSites.Descriptors)
        {
            if (descriptor.ServiceType.IsGenericTypeDefinition) continue; // validated when a closed type is first resolved
            var site = _callSites.GetCallSite(descriptor.ServiceType)!;
            if (_options.ValidateScopes) LifetimeValidator.ThrowIfCaptive(site);
        }
    }
}

internal static class LifetimeValidator
{
    /// <summary>
    /// A "captive dependency" is a singleton holding a scoped service. The singleton lives forever, so
    /// the scoped service it captured (for example a DbContext meant for one request) is silently reused
    /// across all requests and threads. This walks the graph and rejects that.
    /// </summary>
    public static void ThrowIfCaptive(CallSite site) => Visit(site, singletonOwner: null);

    private static void Visit(CallSite site, CallSite? singletonOwner)
    {
        if (site is ServiceProviderCallSite or ConstantCallSite) return;

        if (site.Lifetime == ServiceLifetime.Scoped && singletonOwner is not null)
            throw new InvalidOperationException(
                $"Captive dependency: singleton '{singletonOwner.Describe()}' depends on scoped '{site.Describe()}'. " +
                "The scoped service would be shared by every request. Make the consumer scoped, or the dependency singleton.");

        var owner = singletonOwner ?? (site.Lifetime == ServiceLifetime.Singleton ? site : null);
        foreach (var dependency in site.Dependencies) Visit(dependency, owner);
    }

    public static bool DependsOnScoped(CallSite site) => site switch
    {
        ServiceProviderCallSite or ConstantCallSite => false,
        _ when site.Lifetime == ServiceLifetime.Scoped => true,
        _ when site.Lifetime == ServiceLifetime.Transient => site.Dependencies.Any(DependsOnScoped),
        _ => false, // singletons were already checked for captive dependencies
    };
}

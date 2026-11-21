namespace MiniNet.DependencyInjection;

/// <summary>
/// One scope: caches its scoped instances and disposes everything it created, newest first.
/// The root scope plays the same role for singletons.
/// </summary>
public sealed class ServiceProviderEngineScope : IServiceScope, IServiceProvider
{
    private readonly ServiceProvider _root;
    private readonly Dictionary<object, object?> _resolved = new();
    private readonly List<object> _disposables = [];
    private readonly object _lock = new();
    private bool _disposed;

    internal ServiceProviderEngineScope(ServiceProvider root, bool isRoot)
    {
        _root = root;
        IsRoot = isRoot;
    }

    public bool IsRoot { get; }

    public IServiceProvider ServiceProvider => this;

    public object? GetService(Type serviceType) => _root.Resolve(serviceType, this);

    /// <summary>
    /// Returns the cached instance for a scoped or singleton service, creating it on first use.
    /// Singletons are always created and cached by the root scope, so they never capture a request scope.
    /// </summary>
    internal object? GetOrCreate(CallSite site, Func<ServiceProviderEngineScope, object?> create)
    {
        var owner = site.Lifetime == ServiceLifetime.Singleton ? _root.RootScope : this;
        return owner.GetOrCreateCore(site.CacheKey, create);
    }

    private object? GetOrCreateCore(object key, Func<ServiceProviderEngineScope, object?> create)
    {
        // Holding the lock while creating guarantees one instance per scope even under concurrency.
        // Monitor locks are re-entrant, so creating dependencies in the same scope from inside is fine.
        lock (_lock)
        {
            ThrowIfDisposed();
            if (_resolved.TryGetValue(key, out var existing)) return existing;
            var created = create(this);
            _resolved[key] = created;
            Track(created);
            return created;
        }
    }

    /// <summary>Transient services are not cached, but a scope still disposes the ones it created.</summary>
    internal object? CaptureTransient(object? instance)
    {
        lock (_lock) Track(instance);
        return instance;
    }

    private void Track(object? instance)
    {
        if (instance is IDisposable or IAsyncDisposable && !ReferenceEquals(instance, this) && instance is not DependencyInjection.ServiceProvider)
            _disposables.Add(instance);
    }

    internal void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        foreach (var instance in TakeDisposables())
        {
            if (instance is IDisposable disposable) disposable.Dispose();
            else
                throw new InvalidOperationException(
                    $"'{instance.GetType().Name}' only implements IAsyncDisposable. Dispose the scope with DisposeAsync().");
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var instance in TakeDisposables())
        {
            if (instance is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync();
            else ((IDisposable)instance).Dispose();
        }
    }

    /// <summary>Reverse creation order: a service is disposed before the dependencies it was built from.</summary>
    private List<object> TakeDisposables()
    {
        lock (_lock)
        {
            if (_disposed) return [];
            _disposed = true;
            var result = Enumerable.Reverse(_disposables).ToList();
            _disposables.Clear();
            _resolved.Clear();
            return result;
        }
    }
}

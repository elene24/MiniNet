namespace MiniNet.DependencyInjection;

public enum ServiceLifetime
{
    /// <summary>One instance for the whole application.</summary>
    Singleton,

    /// <summary>One instance per scope. In a web app a scope is one request.</summary>
    Scoped,

    /// <summary>A new instance every time it is resolved.</summary>
    Transient,
}

public sealed class ServiceDescriptor
{
    private ServiceDescriptor(Type serviceType, ServiceLifetime lifetime)
    {
        ServiceType = serviceType;
        Lifetime = lifetime;
    }

    public Type ServiceType { get; }

    public ServiceLifetime Lifetime { get; }

    public Type? ImplementationType { get; private init; }

    public Func<IServiceProvider, object>? Factory { get; private init; }

    public object? Instance { get; private init; }

    public static ServiceDescriptor Describe(Type serviceType, Type implementationType, ServiceLifetime lifetime)
    {
        if (implementationType.IsAbstract || implementationType.IsInterface)
            throw new ArgumentException($"'{implementationType.Name}' is abstract and cannot be constructed.", nameof(implementationType));

        var isOpenGeneric = serviceType.IsGenericTypeDefinition;
        if (isOpenGeneric != implementationType.IsGenericTypeDefinition)
            throw new ArgumentException("An open generic service needs an open generic implementation, and the other way round.");
        if (!isOpenGeneric && !serviceType.IsAssignableFrom(implementationType))
            throw new ArgumentException($"'{implementationType.Name}' does not implement '{serviceType.Name}'.");

        return new ServiceDescriptor(serviceType, lifetime) { ImplementationType = implementationType };
    }

    public static ServiceDescriptor Describe(Type serviceType, Func<IServiceProvider, object> factory, ServiceLifetime lifetime) =>
        new(serviceType, lifetime) { Factory = factory };

    public static ServiceDescriptor Singleton(Type serviceType, object instance) =>
        new(serviceType, ServiceLifetime.Singleton) { Instance = instance };

    public override string ToString() =>
        $"{Lifetime} {ServiceType.Name} -> {ImplementationType?.Name ?? (Instance is not null ? "instance" : "factory")}";
}

/// <summary>A list of registrations. When a service is registered more than once, the last registration wins for single resolution.</summary>
public sealed class ServiceCollection : List<ServiceDescriptor>
{
    public ServiceProvider BuildServiceProvider(ServiceProviderOptions? options = null) => new(this, options ?? new ServiceProviderOptions());
}

public sealed class ServiceProviderOptions
{
    /// <summary>Throw when a singleton depends on a scoped service, or a scoped service is resolved from the root provider.</summary>
    public bool ValidateScopes { get; set; } = true;

    /// <summary>Check every registration when the provider is built, so missing or circular dependencies fail at startup.</summary>
    public bool ValidateOnBuild { get; set; } = true;

    /// <summary>How resolution is performed. Both modes behave identically; they differ only in speed.</summary>
    public ResolutionMode Mode { get; set; } = ResolutionMode.Compiled;
}

public enum ResolutionMode
{
    /// <summary>Call constructors through reflection on every resolution. Simple but slower.</summary>
    Reflection,

    /// <summary>Compile each service's construction into a delegate with expression trees, once.</summary>
    Compiled,
}

public static class ServiceCollectionExtensions
{
    public static ServiceCollection AddSingleton<TService, TImplementation>(this ServiceCollection services) where TImplementation : class, TService =>
        services.AddService(typeof(TService), typeof(TImplementation), ServiceLifetime.Singleton);

    public static ServiceCollection AddScoped<TService, TImplementation>(this ServiceCollection services) where TImplementation : class, TService =>
        services.AddService(typeof(TService), typeof(TImplementation), ServiceLifetime.Scoped);

    public static ServiceCollection AddTransient<TService, TImplementation>(this ServiceCollection services) where TImplementation : class, TService =>
        services.AddService(typeof(TService), typeof(TImplementation), ServiceLifetime.Transient);

    public static ServiceCollection AddSingleton<TService>(this ServiceCollection services) where TService : class =>
        services.AddService(typeof(TService), typeof(TService), ServiceLifetime.Singleton);

    public static ServiceCollection AddScoped<TService>(this ServiceCollection services) where TService : class =>
        services.AddService(typeof(TService), typeof(TService), ServiceLifetime.Scoped);

    public static ServiceCollection AddTransient<TService>(this ServiceCollection services) where TService : class =>
        services.AddService(typeof(TService), typeof(TService), ServiceLifetime.Transient);

    public static ServiceCollection AddSingleton<TService>(this ServiceCollection services, TService instance) where TService : class
    {
        services.Add(ServiceDescriptor.Singleton(typeof(TService), instance));
        return services;
    }

    public static ServiceCollection AddSingleton<TService>(this ServiceCollection services, Func<IServiceProvider, TService> factory) where TService : class =>
        services.AddFactory(typeof(TService), factory, ServiceLifetime.Singleton);

    public static ServiceCollection AddScoped<TService>(this ServiceCollection services, Func<IServiceProvider, TService> factory) where TService : class =>
        services.AddFactory(typeof(TService), factory, ServiceLifetime.Scoped);

    public static ServiceCollection AddTransient<TService>(this ServiceCollection services, Func<IServiceProvider, TService> factory) where TService : class =>
        services.AddFactory(typeof(TService), factory, ServiceLifetime.Transient);

    /// <summary>Non-generic registration, needed for open generics: <c>AddService(typeof(IRepository&lt;&gt;), typeof(Repository&lt;&gt;), ...)</c>.</summary>
    public static ServiceCollection AddService(this ServiceCollection services, Type serviceType, Type implementationType, ServiceLifetime lifetime)
    {
        services.Add(ServiceDescriptor.Describe(serviceType, implementationType, lifetime));
        return services;
    }

    private static ServiceCollection AddFactory(this ServiceCollection services, Type serviceType, Func<IServiceProvider, object> factory, ServiceLifetime lifetime)
    {
        services.Add(ServiceDescriptor.Describe(serviceType, factory, lifetime));
        return services;
    }
}

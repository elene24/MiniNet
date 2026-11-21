namespace MiniNet.DependencyInjection;

public interface IServiceScope : IDisposable, IAsyncDisposable
{
    IServiceProvider ServiceProvider { get; }
}

public interface IServiceScopeFactory
{
    IServiceScope CreateScope();
}

/// <summary>Answers "is this type registered?" without creating it. Endpoint binding uses this to tell services from request bodies.</summary>
public interface IServiceProviderIsService
{
    bool IsService(Type serviceType);
}

public static class ServiceProviderExtensions
{
    public static T? GetService<T>(this IServiceProvider provider) => (T?)provider.GetService(typeof(T));

    public static T GetRequiredService<T>(this IServiceProvider provider) where T : notnull =>
        (T)provider.GetRequiredService(typeof(T));

    public static object GetRequiredService(this IServiceProvider provider, Type serviceType) =>
        provider.GetService(serviceType)
        ?? throw new InvalidOperationException($"No service for type '{TypeNames.Format(serviceType)}' has been registered.");

    public static IEnumerable<T> GetServices<T>(this IServiceProvider provider) =>
        provider.GetRequiredService<IEnumerable<T>>();

    public static IServiceScope CreateScope(this IServiceProvider provider) =>
        provider.GetRequiredService<IServiceScopeFactory>().CreateScope();
}

internal static class TypeNames
{
    /// <summary>"IRepository&lt;Order&gt;" instead of "IRepository`1", for readable error messages.</summary>
    public static string Format(Type type)
    {
        if (!type.IsGenericType) return type.Name;
        var name = type.Name[..type.Name.IndexOf('`')];
        return $"{name}<{string.Join(", ", type.GetGenericArguments().Select(Format))}>";
    }
}

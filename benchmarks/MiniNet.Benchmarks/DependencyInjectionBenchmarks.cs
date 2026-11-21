using BenchmarkDotNet.Attributes;
using MiniNet.DependencyInjection;

namespace MiniNet.Benchmarks;

/// <summary>Reflection-based resolution against compiled expression trees, for the same object graph.</summary>
[MemoryDiagnoser]
public class DependencyInjectionBenchmarks
{
    public interface IClock;

    public sealed class Clock : IClock;

    public sealed class Repository(IClock clock);

    public sealed class Validator;

    public sealed class Notifier(IClock clock);

    public sealed class OrderService(Repository repository, Validator validator, Notifier notifier, IClock clock);

    private ServiceProvider _provider = null!;
    private IServiceScope _scope = null!;

    [Params(ResolutionMode.Reflection, ResolutionMode.Compiled)]
    public ResolutionMode Mode { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IClock, Clock>();
        services.AddScoped<Repository>();
        services.AddTransient<Validator>();
        services.AddTransient<Notifier>();
        services.AddTransient<OrderService>();
        _provider = services.BuildServiceProvider(new ServiceProviderOptions { Mode = Mode });
        _scope = _provider.CreateScope();
        _scope.ServiceProvider.GetRequiredService<OrderService>(); // warm up: build and compile once
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _scope.Dispose();
        _provider.Dispose();
    }

    /// <summary>One transient with a scoped, two transient and one singleton dependency.</summary>
    [Benchmark]
    public object ResolveTransientGraph() => _scope.ServiceProvider.GetRequiredService<OrderService>();

    /// <summary>What every request pays: create a scope, resolve, dispose.</summary>
    [Benchmark]
    public object CreateScopeResolveDispose()
    {
        using var scope = _provider.CreateScope();
        return scope.ServiceProvider.GetRequiredService<OrderService>();
    }
}

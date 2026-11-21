using MiniNet.DependencyInjection;

namespace MiniNet.Tests;

public class DependencyInjectionTests
{
    // ---- test types ----

    public interface IClock;

    public sealed class Clock : IClock;

    public sealed class Repository(IClock clock)
    {
        public IClock Clock { get; } = clock;
    }

    public sealed class OrderService(Repository repository)
    {
        public Repository Repository { get; } = repository;
    }

    public sealed class Tracker
    {
        public List<string> Disposed { get; } = [];
    }

    public sealed class DisposableA(Tracker tracker, DisposableB b) : IDisposable
    {
        public DisposableB B { get; } = b;

        public void Dispose() => tracker.Disposed.Add("A");
    }

    public sealed class DisposableB(Tracker tracker) : IDisposable
    {
        public void Dispose() => tracker.Disposed.Add("B");
    }

    public sealed class AsyncOnly : IAsyncDisposable
    {
        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    public sealed class CycleA(CycleB b);

    public sealed class CycleB(CycleC c);

    public sealed class CycleC(CycleA a);

    public sealed class NeedsMissing(IClock clock);

    public interface IRepository<T>;

    public sealed class Repository<T> : IRepository<T>;

    public interface INotifier;

    public sealed class EmailNotifier : INotifier;

    public sealed class SmsNotifier : INotifier;

    public sealed class WithDefault(IClock clock, int retries = 3)
    {
        public int Retries { get; } = retries;
    }

    public sealed class TwoConstructors
    {
        public TwoConstructors() => Used = "none";

        public TwoConstructors(IClock clock) => Used = "clock";

        public TwoConstructors(IClock clock, Tracker missing) => Used = "both";

        public string Used { get; }
    }

    public sealed class NeedsProvider(IServiceProvider provider)
    {
        public IServiceProvider Provider { get; } = provider;
    }

    private static ServiceProvider Build(ResolutionMode mode, Action<ServiceCollection> configure, bool validateOnBuild = true)
    {
        var services = new ServiceCollection();
        configure(services);
        return services.BuildServiceProvider(new ServiceProviderOptions { Mode = mode, ValidateOnBuild = validateOnBuild });
    }

    // Every behaviour test runs in both modes: compiled expression trees must behave exactly like reflection.

    [Theory]
    [InlineData(ResolutionMode.Reflection)]
    [InlineData(ResolutionMode.Compiled)]
    public void Resolves_a_dependency_graph_through_constructors(ResolutionMode mode)
    {
        using var provider = Build(mode, s => s.AddSingleton<IClock, Clock>().AddTransient<Repository>().AddTransient<OrderService>());

        var service = provider.GetRequiredService<OrderService>();

        Assert.IsType<Clock>(service.Repository.Clock);
    }

    [Theory]
    [InlineData(ResolutionMode.Reflection)]
    [InlineData(ResolutionMode.Compiled)]
    public void Lifetimes_behave_as_documented(ResolutionMode mode)
    {
        using var provider = Build(mode, s => s.AddSingleton<IClock, Clock>().AddScoped<Tracker>().AddTransient<Repository>());

        using var scope1 = provider.CreateScope();
        using var scope2 = provider.CreateScope();
        var p1 = scope1.ServiceProvider;
        var p2 = scope2.ServiceProvider;

        Assert.NotSame(p1.GetRequiredService<Repository>(), p1.GetRequiredService<Repository>()); // transient: always new
        Assert.Same(p1.GetRequiredService<Tracker>(), p1.GetRequiredService<Tracker>());           // scoped: same within a scope
        Assert.NotSame(p1.GetRequiredService<Tracker>(), p2.GetRequiredService<Tracker>());        // ...but not across scopes
        Assert.Same(p1.GetRequiredService<IClock>(), p2.GetRequiredService<IClock>());             // singleton: one instance
    }

    [Theory]
    [InlineData(ResolutionMode.Reflection)]
    [InlineData(ResolutionMode.Compiled)]
    public void Unregistered_services_resolve_to_null(ResolutionMode mode)
    {
        using var provider = Build(mode, _ => { });

        Assert.Null(provider.GetService<IClock>());
        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IClock>());
    }

    [Theory]
    [InlineData(ResolutionMode.Reflection)]
    [InlineData(ResolutionMode.Compiled)]
    public void Resolving_a_scoped_service_from_the_root_provider_is_an_error(ResolutionMode mode)
    {
        using var provider = Build(mode, s => s.AddScoped<Tracker>());

        var error = Assert.Throws<InvalidOperationException>(() => provider.GetService<Tracker>());

        Assert.Contains("root provider", error.Message);
    }

    [Fact]
    public void A_singleton_depending_on_a_scoped_service_is_rejected_at_build_time()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            Build(ResolutionMode.Compiled, s => s.AddScoped<IClock, Clock>().AddSingleton<Repository>()));

        Assert.Contains("Captive dependency", error.Message);
        Assert.Contains("Repository", error.Message);
        Assert.Contains("IClock", error.Message);
    }

    [Fact]
    public void A_captive_dependency_hidden_behind_a_transient_is_still_found()
    {
        // OrderService (singleton) -> Repository (transient) -> IClock (scoped)
        var error = Assert.Throws<InvalidOperationException>(() => Build(ResolutionMode.Compiled, s => s
            .AddScoped<IClock, Clock>()
            .AddTransient<Repository>()
            .AddSingleton<OrderService>()));

        Assert.Contains("Captive dependency", error.Message);
    }

    [Fact]
    public void Circular_dependencies_are_reported_with_the_full_cycle()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            Build(ResolutionMode.Compiled, s => s.AddTransient<CycleA>().AddTransient<CycleB>().AddTransient<CycleC>()));

        Assert.Contains("CycleA -> CycleB -> CycleC -> CycleA", error.Message);
    }

    [Fact]
    public void A_missing_dependency_names_both_types()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Build(ResolutionMode.Compiled, s => s.AddTransient<NeedsMissing>()));

        Assert.Contains("IClock", error.Message);
        Assert.Contains("NeedsMissing", error.Message);
    }

    [Theory]
    [InlineData(ResolutionMode.Reflection)]
    [InlineData(ResolutionMode.Compiled)]
    public void The_last_registration_wins_and_IEnumerable_returns_all_in_order(ResolutionMode mode)
    {
        using var provider = Build(mode, s => s.AddSingleton<INotifier, EmailNotifier>().AddSingleton<INotifier, SmsNotifier>());

        Assert.IsType<SmsNotifier>(provider.GetRequiredService<INotifier>());
        var all = provider.GetServices<INotifier>().ToList();
        Assert.Collection(all, n => Assert.IsType<EmailNotifier>(n), n => Assert.IsType<SmsNotifier>(n));
        Assert.Same(provider.GetRequiredService<INotifier>(), all[1]); // same singleton either way
    }

    [Theory]
    [InlineData(ResolutionMode.Reflection)]
    [InlineData(ResolutionMode.Compiled)]
    public void Open_generic_registrations_close_over_the_requested_type(ResolutionMode mode)
    {
        using var provider = Build(mode, s => s.AddService(typeof(IRepository<>), typeof(Repository<>), ServiceLifetime.Singleton));

        Assert.IsType<Repository<string>>(provider.GetRequiredService<IRepository<string>>());
        Assert.Same(provider.GetRequiredService<IRepository<int>>(), provider.GetRequiredService<IRepository<int>>());
        Assert.NotSame((object)provider.GetRequiredService<IRepository<int>>(), provider.GetRequiredService<IRepository<string>>());
    }

    [Theory]
    [InlineData(ResolutionMode.Reflection)]
    [InlineData(ResolutionMode.Compiled)]
    public void A_scope_disposes_its_services_newest_first(ResolutionMode mode)
    {
        var tracker = new Tracker();
        using var provider = Build(mode, s => s.AddSingleton(tracker).AddScoped<DisposableA>().AddScoped<DisposableB>());

        using (var scope = provider.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<DisposableA>(); // creates B first, then A
        }

        Assert.Equal(new[] { "A", "B" }, tracker.Disposed); // A depends on B, so A must go first
    }

    [Theory]
    [InlineData(ResolutionMode.Reflection)]
    [InlineData(ResolutionMode.Compiled)]
    public void Transient_disposables_are_disposed_with_their_scope(ResolutionMode mode)
    {
        var tracker = new Tracker();
        using var provider = Build(mode, s => s.AddSingleton(tracker).AddTransient<DisposableB>());

        using (var scope = provider.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<DisposableB>();
            scope.ServiceProvider.GetRequiredService<DisposableB>();
        }

        Assert.Equal(new[] { "B", "B" }, tracker.Disposed);
    }

    [Fact]
    public async Task An_async_only_disposable_needs_DisposeAsync()
    {
        var provider = Build(ResolutionMode.Compiled, s => s.AddScoped<AsyncOnly>());

        var syncScope = provider.CreateScope();
        syncScope.ServiceProvider.GetRequiredService<AsyncOnly>();
        Assert.Throws<InvalidOperationException>(() => syncScope.Dispose());

        var asyncScope = provider.CreateScope();
        var service = asyncScope.ServiceProvider.GetRequiredService<AsyncOnly>();
        await asyncScope.DisposeAsync();
        Assert.True(service.Disposed);
    }

    [Fact]
    public void Instances_registered_directly_are_not_disposed_by_the_container()
    {
        var tracker = new Tracker();
        var instance = new DisposableB(tracker);
        var provider = Build(ResolutionMode.Compiled, s => s.AddSingleton(instance));

        provider.GetRequiredService<DisposableB>();
        provider.Dispose();

        Assert.Empty(tracker.Disposed); // the caller created it, so the caller owns it
    }

    [Fact]
    public void A_disposed_scope_refuses_to_resolve()
    {
        var provider = Build(ResolutionMode.Compiled, s => s.AddScoped<Tracker>());
        var scope = provider.CreateScope();
        scope.Dispose();

        Assert.Throws<ObjectDisposedException>(() => scope.ServiceProvider.GetService<Tracker>());
    }

    [Theory]
    [InlineData(ResolutionMode.Reflection)]
    [InlineData(ResolutionMode.Compiled)]
    public void Factories_receive_the_resolving_scope(ResolutionMode mode)
    {
        using var provider = Build(mode, s => s
            .AddSingleton<IClock, Clock>()
            .AddScoped(sp => new Repository(sp.GetRequiredService<IClock>())));

        using var scope = provider.CreateScope();

        Assert.IsType<Clock>(scope.ServiceProvider.GetRequiredService<Repository>().Clock);
    }

    [Theory]
    [InlineData(ResolutionMode.Reflection)]
    [InlineData(ResolutionMode.Compiled)]
    public void Optional_constructor_parameters_use_their_default_value(ResolutionMode mode)
    {
        using var provider = Build(mode, s => s.AddSingleton<IClock, Clock>().AddTransient<WithDefault>());

        Assert.Equal(3, provider.GetRequiredService<WithDefault>().Retries);
    }

    [Fact]
    public void The_longest_satisfiable_constructor_is_chosen()
    {
        using var provider = Build(ResolutionMode.Compiled, s => s.AddSingleton<IClock, Clock>().AddTransient<TwoConstructors>());

        Assert.Equal("clock", provider.GetRequiredService<TwoConstructors>().Used); // (IClock, Tracker) cannot be satisfied
    }

    [Theory]
    [InlineData(ResolutionMode.Reflection)]
    [InlineData(ResolutionMode.Compiled)]
    public void Singletons_receive_the_root_provider_not_a_request_scope(ResolutionMode mode)
    {
        using var provider = Build(mode, s => s.AddSingleton<NeedsProvider>());
        using var scope = provider.CreateScope();

        var singleton = scope.ServiceProvider.GetRequiredService<NeedsProvider>();

        Assert.True(((ServiceProviderEngineScope)singleton.Provider).IsRoot);
    }

    [Fact]
    public async Task Concurrent_resolution_in_one_scope_creates_one_scoped_instance()
    {
        using var provider = Build(ResolutionMode.Compiled, s => s.AddScoped<Tracker>());
        using var scope = provider.CreateScope();

        var instances = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ =>
            Task.Run(() => scope.ServiceProvider.GetRequiredService<Tracker>())));

        Assert.Single(instances.Distinct());
    }

    [Fact]
    public void ActivatorUtilities_mixes_explicit_arguments_with_services()
    {
        using var provider = Build(ResolutionMode.Compiled, s => s.AddSingleton<IClock, Clock>());

        var created = ActivatorUtilities.CreateInstance<WithDefault>(provider, 7);

        Assert.Equal(7, created.Retries);
    }
}

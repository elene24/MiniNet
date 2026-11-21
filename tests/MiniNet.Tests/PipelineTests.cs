using MiniNet.DependencyInjection;
using MiniNet.Http;
using MiniNet.Pipeline;

namespace MiniNet.Tests;

public class PipelineTests
{
    private static readonly ServiceProvider Services = new ServiceCollection().BuildServiceProvider();

    private static HttpContext Context(string path = "/") => new(HttpRequest.Create("GET", path), Services);

    [Fact]
    public async Task Middleware_runs_in_registration_order_on_the_way_in_and_reverse_order_on_the_way_out()
    {
        var log = new List<string>();
        var app = new ApplicationBuilder(Services);
        foreach (var name in new[] { "A", "B", "C" })
        {
            app.Use(async (context, next) =>
            {
                log.Add(name + " in");
                await next(context);
                log.Add(name + " out");
            });
        }

        app.Run(_ =>
        {
            log.Add("endpoint");
            return Task.CompletedTask;
        });

        await app.Build()(Context());

        Assert.Equal(new[] { "A in", "B in", "C in", "endpoint", "C out", "B out", "A out" }, log);
    }

    [Fact]
    public async Task Middleware_that_does_not_call_next_stops_the_pipeline()
    {
        var reachedEndpoint = false;
        var app = new ApplicationBuilder(Services);
        app.Use((context, _) =>
        {
            context.Response.StatusCode = 401;
            return Task.CompletedTask;
        });
        app.Run(_ =>
        {
            reachedEndpoint = true;
            return Task.CompletedTask;
        });

        var context = Context();
        await app.Build()(context);

        Assert.False(reachedEndpoint);
        Assert.Equal(401, context.Response.StatusCode);
    }

    [Fact]
    public async Task An_empty_pipeline_answers_404()
    {
        var context = Context();

        await new ApplicationBuilder(Services).Build()(context);

        Assert.Equal(404, context.Response.StatusCode);
    }

    [Fact]
    public async Task Nothing_registered_after_Run_executes()
    {
        var log = new List<string>();
        var app = new ApplicationBuilder(Services);
        app.Run(_ =>
        {
            log.Add("run");
            return Task.CompletedTask;
        });
        app.Use((context, next) =>
        {
            log.Add("after run");
            return next(context);
        });

        await app.Build()(Context());

        Assert.Equal(new[] { "run" }, log);
    }

    [Fact]
    public async Task Map_branches_on_a_path_prefix_and_moves_it_into_PathBase()
    {
        string? seenPath = null, seenBase = null;
        var app = new ApplicationBuilder(Services);
        app.Map("/api", branch => branch.Run(context =>
        {
            seenPath = context.Request.Path;
            seenBase = context.Request.PathBase;
            return Task.CompletedTask;
        }));

        var context = Context("/api/users");
        await app.Build()(context);

        Assert.Equal("/users", seenPath);
        Assert.Equal("/api", seenBase);
        Assert.Equal("/api/users", context.Request.Path); // restored after the branch
        Assert.Equal("", context.Request.PathBase);
    }

    [Theory]
    [InlineData("/api", true)]
    [InlineData("/api/", true)]
    [InlineData("/API/orders", true)]
    [InlineData("/apikeys", false)] // a prefix match must end at a segment boundary
    [InlineData("/other", false)]
    public async Task Map_matches_whole_segments_only(string path, bool expectBranch)
    {
        var branched = false;
        var app = new ApplicationBuilder(Services);
        app.Map("/api", branch => branch.Run(_ =>
        {
            branched = true;
            return Task.CompletedTask;
        }));

        await app.Build()(Context(path));

        Assert.Equal(expectBranch, branched);
    }

    [Fact]
    public async Task UseWhen_runs_extra_middleware_then_rejoins_the_main_pipeline()
    {
        var log = new List<string>();
        var app = new ApplicationBuilder(Services);
        app.UseWhen(c => c.Request.Path.StartsWith("/admin"), branch => branch.Use((context, next) =>
        {
            log.Add("audit");
            return next(context);
        }));
        app.Run(_ =>
        {
            log.Add("endpoint");
            return Task.CompletedTask;
        });
        var pipeline = app.Build();

        await pipeline(Context("/admin/users"));
        await pipeline(Context("/public"));

        Assert.Equal(new[] { "audit", "endpoint", "endpoint" }, log);
    }

    // ---- class-based middleware ----

    public sealed class Counter
    {
        public int Value { get; set; }
    }

    public sealed class RequestId
    {
        public Guid Value { get; } = Guid.NewGuid();
    }

    public sealed class CountingMiddleware(RequestDelegate next, Counter counter, string label)
    {
        public async Task InvokeAsync(HttpContext context, RequestId requestId)
        {
            counter.Value++;
            context.Response.Headers["X-Label"] = label;
            context.Response.Headers["X-Request-Id"] = requestId.Value.ToString();
            await next(context);
        }
    }

    [Fact]
    public async Task Class_middleware_gets_singletons_in_the_constructor_and_scoped_services_per_request()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Counter>();
        services.AddScoped<RequestId>();
        var provider = services.BuildServiceProvider();

        var app = new ApplicationBuilder(provider);
        app.UseMiddleware<CountingMiddleware>("hello");
        var pipeline = app.Build();

        var ids = new List<string?>();
        for (var i = 0; i < 2; i++)
        {
            await using var scope = provider.CreateScope();
            var context = new HttpContext(HttpRequest.Create("GET", "/"), scope.ServiceProvider);
            await pipeline(context);
            ids.Add(context.Response.Headers["X-Request-Id"]);
            Assert.Equal("hello", context.Response.Headers["X-Label"]);
        }

        Assert.Equal(2, provider.GetRequiredService<Counter>().Value);
        Assert.NotEqual(ids[0], ids[1]); // a new scoped RequestId for each request
    }

    public sealed class BadMiddleware(RequestDelegate next)
    {
        public void InvokeAsync(HttpContext context) => next(context);
    }

    [Fact]
    public void Class_middleware_with_a_wrong_signature_is_rejected()
    {
        var app = new ApplicationBuilder(Services);

        var error = Assert.Throws<InvalidOperationException>(() => app.UseMiddleware<BadMiddleware>().Build());

        Assert.Contains("must return Task", error.Message);
    }
}

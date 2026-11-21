using MiniNet.DependencyInjection;
using MiniNet.Endpoints;
using MiniNet.Http;
using MiniNet.Logging;
using MiniNet.Middleware;
using MiniNet.Routing;

namespace MiniNet.Tests;

public class MiddlewareTests
{
    [Fact]
    public async Task Exception_handling_returns_a_clean_500_and_logs_the_error()
    {
        var logger = new InMemoryLogger();
        await using var app = TestApp.Create(b => b.Services.AddSingleton<ILogger>(logger));
        app.UseExceptionHandling();
        app.MapGet("/boom", async (HttpContext context) =>
        {
            await context.Response.WriteAsync("half a respo");
            throw new InvalidOperationException("database password is hunter2");
        });

        var response = await app.GetAsync("/boom");

        Assert.Equal(500, response.StatusCode);
        Assert.Contains("An unexpected error occurred.", response.BodyText());
        Assert.DoesNotContain("hunter2", response.BodyText()); // internals never leak by default
        Assert.DoesNotContain("half a respo", response.BodyText()); // the partial output was discarded
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Exception is InvalidOperationException);
    }

    [Fact]
    public async Task Exception_details_are_shown_only_when_enabled()
    {
        await using var app = TestApp.Create();
        app.UseExceptionHandling(o => o.IncludeDetails = true);
        app.MapGet("/boom", string () => throw new InvalidOperationException("useful detail"));

        Assert.Contains("useful detail", (await app.GetAsync("/boom")).BodyText());
    }

    [Fact]
    public async Task Without_exception_handling_the_host_still_returns_500_instead_of_crashing()
    {
        await using var app = TestApp.Create();
        app.MapGet("/boom", string () => throw new InvalidOperationException());

        Assert.Equal(500, (await app.GetAsync("/boom")).StatusCode);
    }

    [Fact]
    public async Task Request_logging_records_method_path_and_status()
    {
        var logger = new InMemoryLogger();
        await using var app = TestApp.Create(b => b.Services.AddSingleton<ILogger>(logger));
        app.UseRequestLogging();
        app.MapGet("/ping", () => "pong");

        await app.GetAsync("/ping");
        await app.GetAsync("/missing");

        Assert.Contains(logger.Entries, e => e.Message.StartsWith("GET /ping -> 200"));
        Assert.Contains(logger.Entries, e => e.Message.StartsWith("GET /missing -> 404"));
    }

    private static MiniNet.Hosting.MiniApp RateLimitedApp(ManualTimeProvider clock)
    {
        var app = TestApp.Create();
        app.UseRouting();
        app.UseRateLimiting(o =>
        {
            o.Capacity = 2;
            o.TokensPerSecond = 1;
            o.TimeProvider = clock;
            o.PartitionKey = context => context.Request.Headers["X-Client"] ?? "anonymous";
        });
        app.MapGet("/api", () => "ok");
        app.MapGet("/health", () => "healthy").DisableRateLimiting();
        return app;
    }

    private static Task<HttpResponse> Get(MiniNet.Hosting.MiniApp app, string path, string client = "a") =>
        app.SendAsync(HttpRequest.Create("GET", path, null, ("X-Client", client)));

    [Fact]
    public async Task Rate_limiting_allows_a_burst_then_returns_429_with_Retry_After()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        await using var app = RateLimitedApp(clock);

        Assert.Equal(200, (await Get(app, "/api")).StatusCode);
        Assert.Equal(200, (await Get(app, "/api")).StatusCode);
        var limited = await Get(app, "/api");

        Assert.Equal(429, limited.StatusCode);
        Assert.Equal("1", limited.Headers["Retry-After"]);
        Assert.Equal("0", limited.Headers["X-RateLimit-Remaining"]);
    }

    [Fact]
    public async Task Rate_limit_tokens_refill_over_time()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        await using var app = RateLimitedApp(clock);
        await Get(app, "/api");
        await Get(app, "/api");
        Assert.Equal(429, (await Get(app, "/api")).StatusCode);

        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(200, (await Get(app, "/api")).StatusCode);
        Assert.Equal(429, (await Get(app, "/api")).StatusCode);
    }

    [Fact]
    public async Task Each_client_has_its_own_bucket_and_exempt_endpoints_are_never_limited()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        await using var app = RateLimitedApp(clock);
        for (var i = 0; i < 5; i++) await Get(app, "/api", "a");

        Assert.Equal(429, (await Get(app, "/api", "a")).StatusCode);
        Assert.Equal(200, (await Get(app, "/api", "b")).StatusCode);
        Assert.Equal(200, (await Get(app, "/health", "a")).StatusCode);
    }

    [Fact]
    public async Task Concurrent_requests_never_exceed_the_bucket()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        await using var app = RateLimitedApp(clock);

        var statuses = await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => Task.Run(() => Get(app, "/api"))));

        Assert.Equal(2, statuses.Count(r => r.StatusCode == 200));
    }

    [Fact]
    public async Task Static_files_are_served_and_path_traversal_is_blocked()
    {
        var root = Directory.CreateTempSubdirectory("mininet-static-");
        var site = Directory.CreateDirectory(Path.Combine(root.FullName, "site"));
        await File.WriteAllTextAsync(Path.Combine(site.FullName, "index.html"), "<h1>home</h1>");
        await File.WriteAllTextAsync(Path.Combine(root.FullName, "secret.txt"), "top secret");

        try
        {
            await using var app = TestApp.Create();
            app.UseStaticFiles(site.FullName);

            var index = await app.GetAsync("/");
            Assert.Equal("<h1>home</h1>", index.BodyText());
            Assert.Equal("text/html; charset=utf-8", index.ContentType);

            Assert.Equal(404, (await app.GetAsync("/../secret.txt")).StatusCode);
            Assert.Equal(404, (await app.GetAsync("/%2e%2e/secret.txt")).StatusCode);

            var middleware = new StaticFilesMiddleware(_ => Task.CompletedTask, new StaticFileOptions { RootDirectory = site.FullName });
            Assert.Null(middleware.ResolveFile("/../secret.txt"));
            Assert.Null(middleware.ResolveFile("/../../../../etc/passwd"));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Middleware_after_UseRouting_can_read_the_matched_endpoint()
    {
        await using var app = TestApp.Create();
        app.UseRouting();
        app.Use(async (context, next) =>
        {
            context.Response.Headers["X-Endpoint"] = context.Endpoint?.DisplayName ?? "none";
            await next(context);
        });
        app.MapGet("/orders/{id:int}", (int id) => id).WithName("GetOrder");

        Assert.Equal("GetOrder", (await app.GetAsync("/orders/5")).Headers["X-Endpoint"]);
        Assert.Equal("none", (await app.GetAsync("/nope")).Headers["X-Endpoint"]);
    }
}

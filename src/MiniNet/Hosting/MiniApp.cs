using System.Runtime.InteropServices;
using MiniNet.DependencyInjection;
using MiniNet.Endpoints;
using MiniNet.Http;
using MiniNet.Logging;
using MiniNet.Pipeline;
using MiniNet.Routing;
using MiniNet.Server;

namespace MiniNet.Hosting;

/// <summary>
/// Configures services and server settings before the app is built. Mirrors ASP.NET Core's
/// <c>WebApplication.CreateBuilder</c>: register services, call <see cref="Build"/>, then set up the pipeline.
/// </summary>
public sealed class MiniAppBuilder
{
    internal MiniAppBuilder(string[] args)
    {
        EnvironmentName = ReadSetting(args, "environment", "MININET_ENVIRONMENT") ?? "Production";
        if (ReadSetting(args, "port", "PORT") is { } port) Server.Port = int.Parse(port, System.Globalization.CultureInfo.InvariantCulture);
    }

    public ServiceCollection Services { get; } = new();

    public ServerOptions Server { get; } = new();

    public ServiceProviderOptions ServiceProviderOptions { get; } = new();

    /// <summary>"Development" or "Production". Read from --environment or the MININET_ENVIRONMENT variable.</summary>
    public string EnvironmentName { get; set; }

    public bool IsDevelopment => string.Equals(EnvironmentName, "Development", StringComparison.OrdinalIgnoreCase);

    public MiniApp Build()
    {
        // Defaults, unless the app registered its own.
        if (!Services.Any(d => d.ServiceType == typeof(ILogger))) Services.AddSingleton<ILogger>(new ConsoleLogger());
        if (!Services.Any(d => d.ServiceType == typeof(TimeProvider))) Services.AddSingleton(TimeProvider.System);

        return new MiniApp(Services.BuildServiceProvider(ServiceProviderOptions), Server, EnvironmentName);
    }

    /// <summary>Command-line arguments ("--port 5000" or "--port=5000") win over environment variables.</summary>
    private static string? ReadSetting(string[] args, string argumentName, string environmentVariable)
    {
        var flag = "--" + argumentName;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith(flag + "=", StringComparison.OrdinalIgnoreCase)) return args[i][(flag.Length + 1)..];
            if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) return args[i + 1];
        }

        return Environment.GetEnvironmentVariable(environmentVariable);
    }
}

/// <summary>
/// The application: a pipeline builder, an endpoint route builder, and the host that connects
/// them to <see cref="MiniServer"/>. Each request gets its own DI scope, disposed after the response is ready.
/// </summary>
public sealed class MiniApp : ApplicationBuilder, IEndpointRouteBuilder, IAsyncDisposable
{
    private readonly ServerOptions _serverOptions;
    private readonly object _buildLock = new();
    private RequestDelegate? _pipeline;
    private MiniServer? _server;

    internal MiniApp(ServiceProvider services, ServerOptions serverOptions, string environmentName)
        : base(services)
    {
        Services = services;
        _serverOptions = serverOptions;
        EnvironmentName = environmentName;
        Logger = services.GetRequiredService<ILogger>();
        Properties[RoutingExtensions.DataSourceKey] = DataSource;
    }

    public static MiniAppBuilder CreateBuilder(string[]? args = null) => new(args ?? []);

    public ServiceProvider Services { get; }

    public EndpointDataSource DataSource { get; } = new();

    public ILogger Logger { get; }

    public string EnvironmentName { get; }

    public bool IsDevelopment => string.Equals(EnvironmentName, "Development", StringComparison.OrdinalIgnoreCase);

    /// <summary>The port in use once started (useful when <see cref="ServerOptions.Port"/> is 0).</summary>
    public int Port => _server?.Port ?? _serverOptions.Port;

    public string Url => $"http://localhost:{Port}";

    IServiceProvider IEndpointRouteBuilder.ServiceProvider => Services;

    /// <summary>
    /// Builds the final pipeline once. Like ASP.NET Core's WebApplication, routing is added automatically
    /// at the very start unless the app placed UseRouting itself, and endpoint execution always comes last.
    /// Building the router here also validates every route, so ambiguous routes fail at startup.
    /// </summary>
    public RequestDelegate BuildPipeline()
    {
        lock (_buildLock)
        {
            if (_pipeline is not null) return _pipeline;

            Use(RoutingExtensions.EndpointExecutor);
            var pipeline = Build();

            if (!Properties.ContainsKey(RoutingExtensions.RoutingAddedKey))
            {
                var routing = new ApplicationBuilder(Services);
                routing.Properties[RoutingExtensions.DataSourceKey] = DataSource;
                routing.UseRouting();
                routing.Run(pipeline);
                pipeline = routing.Build();
            }

            _ = DataSource.Router;
            return _pipeline = pipeline;
        }
    }

    public async Task StartAsync()
    {
        var pipeline = BuildPipeline();
        _server = new MiniServer(_serverOptions, (request, ip, aborted) => HandleAsync(pipeline, request, ip, aborted));
        await _server.StartAsync();
        Logger.Info($"MiniNet listening on {Url} ({EnvironmentName})");
    }

    public async Task StopAsync()
    {
        if (_server is null) return;
        Logger.Info("Shutting down: finishing in-flight requests...");
        await _server.StopAsync();
        _server = null;
    }

    /// <summary>Starts the server and blocks until Ctrl+C, SIGTERM (sent by Docker and Azure on shutdown), or <paramref name="cancellationToken"/>.</summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var shutdown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true;
            shutdown.TrySetResult();
        };

        Console.CancelKeyPress += onCancel;
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            shutdown.TrySetResult();
        });
        await using var registration = cancellationToken.Register(() => shutdown.TrySetResult());

        try
        {
            await StartAsync();
            await shutdown.Task;
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
            await StopAsync();
            await Services.DisposeAsync();
        }
    }

    /// <summary>
    /// Sends a request through the full pipeline without a network connection. Tests use this the way
    /// ASP.NET Core tests use TestServer: fast, and everything except the socket layer is real.
    /// </summary>
    public Task<HttpResponse> SendAsync(HttpRequest request, CancellationToken cancellationToken = default) =>
        HandleAsync(BuildPipeline(), request, remoteIpAddress: null, cancellationToken);

    private async Task<HttpResponse> HandleAsync(RequestDelegate pipeline, HttpRequest request, System.Net.IPAddress? remoteIpAddress, CancellationToken aborted)
    {
        await using var scope = (IServiceScope)Services.CreateScope();
        var context = new HttpContext(request, scope.ServiceProvider, remoteIpAddress, aborted);
        try
        {
            await pipeline(context);
        }
        catch (Exception ex)
        {
            // Only reached when the app has no UseExceptionHandling. Never let one request take down the connection loop.
            Logger.Error($"Unhandled exception for {request.Method} {request.Path}", ex);
            context.Response.Clear();
            context.Response.StatusCode = 500;
        }

        return context.Response;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        await Services.DisposeAsync();
    }
}

using MiniNet.Http;
using MiniNet.Pipeline;

namespace MiniNet.Routing;

/// <summary>Holds all registered endpoints and builds the <see cref="Router"/> once, on first use.</summary>
public sealed class EndpointDataSource
{
    private readonly List<Endpoint> _endpoints = [];
    private readonly object _lock = new();
    private Router? _router;

    public IReadOnlyList<Endpoint> Endpoints => _endpoints;

    public void Add(Endpoint endpoint)
    {
        lock (_lock)
        {
            if (_router is not null)
                throw new InvalidOperationException("Endpoints cannot be added after the application has started.");
            _endpoints.Add(endpoint);
        }
    }

    /// <summary>Building the router validates every template, so ambiguous routes fail at startup, not on some later request.</summary>
    public Router Router
    {
        get
        {
            if (_router is not null) return _router;
            lock (_lock) return _router ??= new Router(_endpoints);
        }
    }
}

public static class RoutingExtensions
{
    internal const string DataSourceKey = "MiniNet.EndpointDataSource";
    internal const string RoutingAddedKey = "MiniNet.RoutingAdded";
    private const string AllowedMethodsKey = "MiniNet.AllowedMethods";

    /// <summary>
    /// Finds the endpoint for the request and stores it in <see cref="HttpContext.Endpoint"/>, but does not run it yet.
    /// Middleware placed between UseRouting and the end of the pipeline can therefore see which endpoint
    /// will run and read its metadata. That is why authorization has to come after routing.
    /// If you don't call this, MiniApp adds it automatically as the first middleware.
    /// </summary>
    public static ApplicationBuilder UseRouting(this ApplicationBuilder app)
    {
        var dataSource = GetDataSource(app);
        app.Properties[RoutingAddedKey] = true;

        return app.Use(next => context =>
        {
            var match = dataSource.Router.Match(context.Request.Path, context.Request.Method);
            if (match.Endpoint is not null)
            {
                context.Endpoint = match.Endpoint;
                foreach (var (name, value) in match.Values) context.Request.RouteValues[name] = value;
            }
            else if (match.AllowedMethods.Count > 0)
            {
                context.Items[AllowedMethodsKey] = match.AllowedMethods;
            }

            return next(context);
        });
    }

    /// <summary>The last stage of the pipeline: runs the matched endpoint, answers 405, or falls through to 404.</summary>
    internal static RequestDelegate EndpointExecutor(RequestDelegate next) => context =>
    {
        if (context.Endpoint is { } endpoint) return endpoint.RequestDelegate(context);

        if (context.Items.TryGetValue(AllowedMethodsKey, out var value) && value is IReadOnlyCollection<string> allowed)
        {
            context.Response.StatusCode = 405;
            context.Response.Headers["Allow"] = string.Join(", ", allowed);
            return Task.CompletedTask;
        }

        return next(context);
    };

    internal static EndpointDataSource GetDataSource(ApplicationBuilder app) =>
        app.Properties.TryGetValue(DataSourceKey, out var value) && value is EndpointDataSource dataSource
            ? dataSource
            : throw new InvalidOperationException("No endpoint data source. Create the application with MiniApp.CreateBuilder().");
}

using MiniNet.Pipeline;
using MiniNet.Routing;

namespace MiniNet.Middleware;

public static class BuiltInMiddlewareExtensions
{
    public static ApplicationBuilder UseExceptionHandling(this ApplicationBuilder app, Action<ExceptionHandlingOptions>? configure = null)
    {
        var options = new ExceptionHandlingOptions();
        configure?.Invoke(options);
        return app.UseMiddleware<ExceptionHandlingMiddleware>(options);
    }

    public static ApplicationBuilder UseRequestLogging(this ApplicationBuilder app) =>
        app.UseMiddleware<RequestLoggingMiddleware>();

    public static ApplicationBuilder UseRateLimiting(this ApplicationBuilder app, Action<RateLimitingOptions>? configure = null)
    {
        var options = new RateLimitingOptions();
        configure?.Invoke(options);
        return app.UseMiddleware<RateLimitingMiddleware>(options);
    }

    public static ApplicationBuilder UseStaticFiles(this ApplicationBuilder app, string rootDirectory) =>
        app.UseMiddleware<StaticFilesMiddleware>(new StaticFileOptions { RootDirectory = rootDirectory });

    /// <summary>Exempts an endpoint from <see cref="RateLimitingMiddleware"/>.</summary>
    public static EndpointBuilder DisableRateLimiting(this EndpointBuilder endpoint) =>
        endpoint.WithMetadata(DisableRateLimitingMetadata.Instance);
}

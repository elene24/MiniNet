using System.Diagnostics;
using MiniNet.Http;
using MiniNet.Logging;
using MiniNet.Pipeline;

namespace MiniNet.Middleware;

/// <summary>Logs one line per request with method, path, status code and duration.</summary>
public sealed class RequestLoggingMiddleware(RequestDelegate next, ILogger logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            await next(context);
        }
        finally
        {
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var request = context.Request;
            logger.Info($"{request.Method} {request.PathBase}{request.Path} -> {context.Response.StatusCode} in {elapsed:0.0} ms");
        }
    }
}

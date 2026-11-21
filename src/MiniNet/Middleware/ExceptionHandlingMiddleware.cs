using MiniNet.Endpoints;
using MiniNet.Http;
using MiniNet.Logging;
using MiniNet.Pipeline;

namespace MiniNet.Middleware;

public sealed class ExceptionHandlingOptions
{
    /// <summary>Include the exception message in the response. Only turn this on in development: it can leak internals.</summary>
    public bool IncludeDetails { get; set; }
}

/// <summary>
/// Catches anything thrown further down the pipeline, logs it, and returns a clean 500 problem response.
/// Register it first, so it wraps everything else.
/// </summary>
public sealed class ExceptionHandlingMiddleware(RequestDelegate next, ILogger logger, ExceptionHandlingOptions options)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The client went away or the server is shutting down. Not an application error.
            context.Response.Clear();
            context.Response.StatusCode = 503;
        }
        catch (Exception ex)
        {
            logger.Error($"Unhandled exception for {context.Request.Method} {context.Request.PathBase}{context.Request.Path} (trace {context.TraceIdentifier})", ex);

            // Throw away whatever the failed handler half-wrote, so the client never sees a mixed response.
            context.Response.Clear();
            var detail = options.IncludeDetails ? ex.Message : null;
            await Results.Problem(500, "An unexpected error occurred.", detail).ExecuteAsync(context);
        }
    }
}

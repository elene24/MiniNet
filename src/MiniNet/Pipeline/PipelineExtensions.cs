using MiniNet.Http;

namespace MiniNet.Pipeline;

public static class PipelineExtensions
{
    /// <summary>
    /// Branches the pipeline for requests whose path starts with <paramref name="pathPrefix"/>.
    /// The branch does not rejoin the main pipeline. Inside the branch the prefix moves from
    /// <see cref="HttpRequest.Path"/> to <see cref="HttpRequest.PathBase"/>, so "/api/users" is seen as "/users".
    /// </summary>
    public static ApplicationBuilder Map(this ApplicationBuilder app, string pathPrefix, Action<ApplicationBuilder> configure)
    {
        if (!pathPrefix.StartsWith('/') || pathPrefix.EndsWith('/'))
            throw new ArgumentException("Path prefix must start with '/' and not end with '/'.", nameof(pathPrefix));

        var branchBuilder = app.New();
        configure(branchBuilder);
        var branch = branchBuilder.Build();

        return app.Use(next => async context =>
        {
            var request = context.Request;
            if (!StartsWithSegments(request.Path, pathPrefix, out var remaining))
            {
                await next(context);
                return;
            }

            var originalPath = request.Path;
            var originalPathBase = request.PathBase;
            request.PathBase = originalPathBase + pathPrefix;
            request.Path = remaining;
            try
            {
                await branch(context);
            }
            finally
            {
                request.Path = originalPath;
                request.PathBase = originalPathBase;
            }
        });
    }

    /// <summary>
    /// Runs extra middleware only when <paramref name="predicate"/> is true, then rejoins the main pipeline.
    /// </summary>
    public static ApplicationBuilder UseWhen(this ApplicationBuilder app, Func<HttpContext, bool> predicate, Action<ApplicationBuilder> configure)
    {
        return app.Use(next =>
        {
            var branchBuilder = app.New();
            configure(branchBuilder);
            branchBuilder.Run(next); // the branch ends by continuing the main pipeline
            var branch = branchBuilder.Build();

            return context => predicate(context) ? branch(context) : next(context);
        });
    }

    /// <summary>"/api" matches "/api" and "/api/users" but not "/apikeys".</summary>
    internal static bool StartsWithSegments(string path, string prefix, out string remaining)
    {
        remaining = path;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        if (path.Length == prefix.Length)
        {
            remaining = "/";
            return true;
        }

        if (path[prefix.Length] != '/') return false;
        remaining = path[prefix.Length..];
        return true;
    }
}

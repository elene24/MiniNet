using MiniNet.Http;

namespace MiniNet.Pipeline;

/// <summary>
/// The heart of the request pipeline: a request is handled by a function, and a middleware is a
/// function that takes "the rest of the pipeline" and returns a new function wrapping it.
/// </summary>
public delegate Task RequestDelegate(HttpContext context);

/// <summary>
/// Collects middleware in the order they are registered and composes them into one
/// <see cref="RequestDelegate"/>.
/// </summary>
public class ApplicationBuilder
{
    private readonly List<Func<RequestDelegate, RequestDelegate>> _components = [];

    public ApplicationBuilder(IServiceProvider applicationServices)
    {
        ApplicationServices = applicationServices;
    }

    /// <summary>The root (singleton) service provider, used to construct class-based middleware.</summary>
    public IServiceProvider ApplicationServices { get; }

    /// <summary>Shared state for extension methods, for example "has routing been added?".</summary>
    public Dictionary<string, object?> Properties { get; } = new();

    /// <summary>The lowest-level way to add middleware: a function from "next" to a new delegate.</summary>
    public ApplicationBuilder Use(Func<RequestDelegate, RequestDelegate> middleware)
    {
        _components.Add(middleware);
        return this;
    }

    /// <summary>Inline middleware. Call <c>next(context)</c> to continue, or return without calling it to stop the pipeline.</summary>
    public ApplicationBuilder Use(Func<HttpContext, RequestDelegate, Task> middleware) =>
        Use(next => context => middleware(context, next));

    /// <summary>Adds a terminal handler. Anything registered after it never runs.</summary>
    public void Run(RequestDelegate handler) => Use(_ => handler);

    /// <summary>Creates a new, empty builder that shares services and properties, for building a branch.</summary>
    public virtual ApplicationBuilder New()
    {
        var branch = new ApplicationBuilder(ApplicationServices);
        foreach (var (key, value) in Properties) branch.Properties[key] = value;
        return branch;
    }

    /// <summary>
    /// Composes the pipeline. The loop runs from the last middleware to the first: the last one
    /// wraps the terminal 404 handler, the one before it wraps that, and so on. The result is that
    /// the first registered middleware is the outermost layer, so it sees the request first and the
    /// response last.
    /// </summary>
    public RequestDelegate Build()
    {
        RequestDelegate app = context =>
        {
            context.Response.StatusCode = 404;
            return Task.CompletedTask;
        };

        for (var i = _components.Count - 1; i >= 0; i--)
            app = _components[i](app);

        return app;
    }
}

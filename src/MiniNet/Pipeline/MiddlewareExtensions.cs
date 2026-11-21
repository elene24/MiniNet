using System.Reflection;
using MiniNet.DependencyInjection;
using MiniNet.Http;

namespace MiniNet.Pipeline;

public static class MiddlewareExtensions
{
    /// <summary>
    /// Adds class-based middleware, following the same convention as ASP.NET Core:
    /// <list type="bullet">
    /// <item>The constructor receives the next <see cref="RequestDelegate"/>, any <paramref name="arguments"/>, and singleton services.</item>
    /// <item>A public <c>InvokeAsync</c> (or <c>Invoke</c>) method takes <see cref="HttpContext"/> first and returns <see cref="Task"/>.</item>
    /// <item>Extra <c>InvokeAsync</c> parameters are resolved from the request scope on every request.</item>
    /// </list>
    /// The middleware object is created once, when the pipeline is built. That is why scoped services
    /// must go in <c>InvokeAsync</c>, not the constructor: a constructor would capture one request's instance forever.
    /// </summary>
    public static ApplicationBuilder UseMiddleware<TMiddleware>(this ApplicationBuilder app, params object[] arguments) =>
        app.UseMiddleware(typeof(TMiddleware), arguments);

    public static ApplicationBuilder UseMiddleware(this ApplicationBuilder app, Type middlewareType, params object[] arguments)
    {
        var invokeMethods = middlewareType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name is "InvokeAsync" or "Invoke")
            .ToList();

        if (invokeMethods.Count != 1)
            throw new InvalidOperationException($"Middleware '{middlewareType.Name}' must have exactly one public InvokeAsync or Invoke method.");

        var invoke = invokeMethods[0];
        var parameters = invoke.GetParameters();
        if (invoke.ReturnType != typeof(Task) || parameters.Length == 0 || parameters[0].ParameterType != typeof(HttpContext))
            throw new InvalidOperationException($"'{middlewareType.Name}.{invoke.Name}' must return Task and take HttpContext as its first parameter.");

        return app.Use(next =>
        {
            var instance = ActivatorUtilities.CreateInstance(app.ApplicationServices, middlewareType, [next, .. arguments]);

            // Common case: InvokeAsync(HttpContext) only. Bind it directly, with no per-request reflection.
            if (parameters.Length == 1)
                return (RequestDelegate)Delegate.CreateDelegate(typeof(RequestDelegate), instance, invoke);

            return context =>
            {
                var values = new object?[parameters.Length];
                values[0] = context;
                for (var i = 1; i < parameters.Length; i++)
                    values[i] = context.RequestServices.GetRequiredService(parameters[i].ParameterType);
                return (Task)invoke.Invoke(instance, BindingFlags.DoNotWrapExceptions, binder: null, values, culture: null)!;
            };
        });
    }
}

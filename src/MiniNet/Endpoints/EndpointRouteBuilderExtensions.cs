using MiniNet.DependencyInjection;
using MiniNet.Pipeline;
using MiniNet.Routing;

namespace MiniNet.Endpoints;

public interface IEndpointRouteBuilder
{
    EndpointDataSource DataSource { get; }

    IServiceProvider ServiceProvider { get; }
}

public static class EndpointRouteBuilderExtensions
{
    public static EndpointBuilder MapGet(this IEndpointRouteBuilder endpoints, string template, Delegate handler) =>
        endpoints.MapMethod("GET", template, handler);

    public static EndpointBuilder MapPost(this IEndpointRouteBuilder endpoints, string template, Delegate handler) =>
        endpoints.MapMethod("POST", template, handler);

    public static EndpointBuilder MapPut(this IEndpointRouteBuilder endpoints, string template, Delegate handler) =>
        endpoints.MapMethod("PUT", template, handler);

    public static EndpointBuilder MapPatch(this IEndpointRouteBuilder endpoints, string template, Delegate handler) =>
        endpoints.MapMethod("PATCH", template, handler);

    public static EndpointBuilder MapDelete(this IEndpointRouteBuilder endpoints, string template, Delegate handler) =>
        endpoints.MapMethod("DELETE", template, handler);

    public static EndpointBuilder MapMethod(this IEndpointRouteBuilder endpoints, string httpMethod, string template, Delegate handler)
    {
        var routeTemplate = RouteTemplate.Parse(template);

        // A plain RequestDelegate is used as-is. Anything else goes through parameter binding.
        var requestDelegate = handler as RequestDelegate ?? RequestDelegateFactory.Create(
            handler, routeTemplate, endpoints.ServiceProvider.GetRequiredService<IServiceProviderIsService>());

        var endpoint = new Endpoint(httpMethod, routeTemplate, requestDelegate);
        endpoints.DataSource.Add(endpoint);
        return new EndpointBuilder(endpoint);
    }
}

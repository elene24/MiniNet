using MiniNet.Pipeline;

namespace MiniNet.Routing;

/// <summary>A route template plus HTTP method plus the delegate that handles it.</summary>
public sealed class Endpoint
{
    public Endpoint(string httpMethod, RouteTemplate template, RequestDelegate requestDelegate, string? displayName = null)
    {
        HttpMethod = httpMethod.ToUpperInvariant();
        Template = template;
        RequestDelegate = requestDelegate;
        DisplayName = displayName ?? $"{HttpMethod} {template.Text}";
    }

    public string HttpMethod { get; }

    public RouteTemplate Template { get; }

    public RequestDelegate RequestDelegate { get; }

    public string DisplayName { get; internal set; }

    /// <summary>
    /// Extra information middleware can read after routing, for example a rate-limit policy or
    /// "requires authentication". This is why routing runs before such middleware.
    /// </summary>
    public List<object> Metadata { get; } = [];

    public T? GetMetadata<T>() where T : class => Metadata.OfType<T>().LastOrDefault();

    public override string ToString() => DisplayName;
}

/// <summary>Returned by MapGet and friends, so endpoints can be configured fluently.</summary>
public sealed class EndpointBuilder(Endpoint endpoint)
{
    public Endpoint Endpoint { get; } = endpoint;

    public EndpointBuilder WithName(string name)
    {
        Endpoint.DisplayName = name;
        return this;
    }

    public EndpointBuilder WithMetadata(params object[] items)
    {
        Endpoint.Metadata.AddRange(items);
        return this;
    }
}

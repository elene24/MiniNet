using System.Net;
using MiniNet.Routing;

namespace MiniNet.Http;

public sealed class HttpContext
{
    private static long _nextId;

    public HttpContext(HttpRequest request, IServiceProvider requestServices, IPAddress? remoteIpAddress = null, CancellationToken requestAborted = default)
    {
        Request = request;
        RequestServices = requestServices;
        RemoteIpAddress = remoteIpAddress;
        RequestAborted = requestAborted;
        TraceIdentifier = Interlocked.Increment(ref _nextId).ToString("x8");
    }

    public HttpRequest Request { get; }

    public HttpResponse Response { get; } = new();

    /// <summary>The per-request DI scope. Scoped services resolved here live until the response is sent.</summary>
    public IServiceProvider RequestServices { get; set; }

    public IPAddress? RemoteIpAddress { get; }

    public CancellationToken RequestAborted { get; }

    public string TraceIdentifier { get; }

    /// <summary>Set by the routing middleware once a route matches; read by later middleware and the endpoint executor.</summary>
    public Endpoint? Endpoint { get; set; }

    /// <summary>Free-form storage for passing data between middleware during one request.</summary>
    public Dictionary<object, object?> Items { get; } = new();
}

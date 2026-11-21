using System.Net;

namespace MiniNet.Server;

public sealed class ServerOptions
{
    /// <summary>Port to listen on. 0 asks the operating system for a free port, which is what the tests use.</summary>
    public int Port { get; set; } = 8080;

    public IPAddress Address { get; set; } = IPAddress.Any;

    public ServerLimits Limits { get; } = new();

    /// <summary>How long to let in-flight requests finish after shutdown is requested.</summary>
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(10);
}

public sealed class ServerLimits
{
    /// <summary>Longest accepted request line ("GET /path HTTP/1.1"). Longer lines get 414 URI Too Long.</summary>
    public int MaxRequestLineBytes { get; set; } = 8 * 1024;

    /// <summary>Total size of all header lines. Larger header sections get 431.</summary>
    public int MaxHeaderBytes { get; set; } = 32 * 1024;

    public int MaxHeaderCount { get; set; } = 100;

    /// <summary>Largest accepted body. Larger bodies get 413 Content Too Large.</summary>
    public long MaxBodyBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>
    /// Time allowed to receive a full request once its first byte arrives. This defends against
    /// "slowloris" clients that send one header byte at a time to hold connections open.
    /// </summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>How long an idle keep-alive connection stays open waiting for the next request.</summary>
    public TimeSpan KeepAliveTimeout { get; set; } = TimeSpan.FromSeconds(30);
}

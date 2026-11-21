using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using MiniNet.Http;

namespace MiniNet.Server;

/// <summary>Called by the server once per parsed request. Must return a response and should not throw.</summary>
public delegate Task<HttpResponse> RequestHandler(HttpRequest request, IPAddress? remoteIpAddress, CancellationToken requestAborted);

/// <summary>
/// The transport layer: accepts TCP connections, reads bytes with <see cref="PipeReader"/>,
/// hands complete requests to the application, and writes responses back.
///
/// Each connection runs its own loop so that keep-alive connections can carry several requests,
/// including pipelined ones that arrive in the same read.
/// </summary>
public sealed class MiniServer : IAsyncDisposable
{
    private readonly ServerOptions _options;
    private readonly RequestHandler _handler;
    private readonly CancellationTokenSource _stopping = new();

    // Separate from _stopping: shutdown stops new requests at once, but in-flight requests are only
    // aborted if they are still running when the shutdown timeout expires.
    private readonly CancellationTokenSource _abortRequests = new();
    private readonly ConcurrentDictionary<long, (Socket Socket, Task Task)> _connections = new();
    private TcpListener? _listener;
    private Task? _acceptLoop;
    private long _nextConnectionId;

    public MiniServer(ServerOptions options, RequestHandler handler)
    {
        _options = options;
        _handler = handler;
    }

    /// <summary>The port actually bound. Differs from <see cref="ServerOptions.Port"/> when that is 0.</summary>
    public int Port { get; private set; }

    public int ActiveConnections => _connections.Count;

    public Task StartAsync()
    {
        // "Any address" listens on IPv6 and IPv4 together (dual mode). Otherwise, on Windows, "localhost"
        // resolves to the IPv6 address ::1 first, finds nothing listening there, and only falls back to
        // 127.0.0.1 after about two seconds. TcpListener.Create uses IPv4 only if the OS has no IPv6.
        _listener = _options.Address.Equals(IPAddress.Any) || _options.Address.Equals(IPAddress.IPv6Any)
            ? TcpListener.Create(_options.Port)
            : new TcpListener(_options.Address, _options.Port);
        _listener.Start(backlog: 512);
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = AcceptLoopAsync();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Graceful shutdown: stop accepting, close idle connections, let requests already being
    /// handled finish, and force-close whatever is left after <see cref="ServerOptions.ShutdownTimeout"/>.
    /// </summary>
    public async Task StopAsync()
    {
        if (_stopping.IsCancellationRequested) return;
        _stopping.Cancel();
        _listener?.Stop();
        if (_acceptLoop is not null) await _acceptLoop;

        var remaining = Task.WhenAll(_connections.Values.Select(c => c.Task));
        var finished = await Task.WhenAny(remaining, Task.Delay(_options.ShutdownTimeout));
        if (finished != remaining)
        {
            _abortRequests.Cancel();
            foreach (var (socket, _) in _connections.Values) socket.Dispose();
            await remaining.ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _stopping.Dispose();
        _abortRequests.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await _listener!.AcceptSocketAsync(_stopping.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                break;
            }

            socket.NoDelay = true;
            var id = Interlocked.Increment(ref _nextConnectionId);
            // Register before starting so that StopAsync always sees the connection.
            var start = new TaskCompletionSource();
            var task = RunConnectionAsync(id, socket, start.Task);
            _connections[id] = (socket, task);
            start.SetResult();
        }
    }

    private async Task RunConnectionAsync(long id, Socket socket, Task registered)
    {
        await registered;
        try
        {
            await HandleConnectionAsync(socket);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            // The client disconnected or the socket was force-closed during shutdown. Nothing to report.
        }
        finally
        {
            socket.Dispose();
            _connections.TryRemove(id, out _);
        }
    }

    private async Task HandleConnectionAsync(Socket socket)
    {
        await using var stream = new NetworkStream(socket, ownsSocket: false);
        var reader = PipeReader.Create(stream);
        var remoteIp = (socket.RemoteEndPoint as IPEndPoint)?.Address;
        // On a dual-mode socket IPv4 clients appear as "::ffff:1.2.3.4". Convert back so that
        // logging and per-IP rate limiting see the familiar IPv4 form.
        if (remoteIp is { IsIPv4MappedToIPv6: true }) remoteIp = remoteIp.MapToIPv4();
        var limits = _options.Limits;

        try
        {
            while (true)
            {
                // ---- Read one complete request ----
                HttpRequest? request = null;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
                timeout.CancelAfter(limits.KeepAliveTimeout);
                var requestStarted = false;

                while (request is null)
                {
                    ReadResult read;
                    try
                    {
                        read = await reader.ReadAsync(timeout.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        // Idle keep-alive connection or shutdown: just close. Half-received request: tell the client why.
                        if (requestStarted && !_stopping.IsCancellationRequested)
                            await stream.WriteAsync(HttpResponseWriter.SerializeError(408, "Request timed out."));
                        return;
                    }

                    var buffer = read.Buffer;
                    if (!requestStarted && buffer.Length > 0)
                    {
                        // The clock for receiving the full request starts at its first byte.
                        requestStarted = true;
                        timeout.CancelAfter(limits.RequestTimeout);
                    }

                    var result = HttpRequestParser.TryParse(ref buffer, limits);
                    switch (result.Status)
                    {
                        case ParseStatus.Complete:
                            // Consume exactly this request. Any pipelined bytes after it stay in the pipe.
                            reader.AdvanceTo(buffer.Start);
                            request = result.Request;
                            break;

                        case ParseStatus.Error:
                            await stream.WriteAsync(HttpResponseWriter.SerializeError(result.ErrorStatusCode, result.ErrorMessage ?? "Bad request."));
                            return;

                        default:
                            // Consumed nothing, but examined everything: the next ReadAsync waits for new bytes.
                            reader.AdvanceTo(read.Buffer.Start, read.Buffer.End);
                            if (read.IsCompleted) return; // client closed mid-request
                            break;
                    }
                }

                // ---- Run the application ----
                var keepAlive = ShouldKeepAlive(request) && !_stopping.IsCancellationRequested;
                HttpResponse response;
                try
                {
                    response = await _handler(request, remoteIp, _abortRequests.Token);
                }
                catch (Exception)
                {
                    // The hosting layer handles exceptions; this is a last line of defence.
                    response = new HttpResponse { StatusCode = 500 };
                    keepAlive = false;
                }

                if (string.Equals(response.Headers["Connection"], "close", StringComparison.OrdinalIgnoreCase))
                    keepAlive = false;

                var bytes = HttpResponseWriter.Serialize(response, keepAlive, request.Method == "HEAD");
                await stream.WriteAsync(bytes);

                if (!keepAlive) return;
            }
        }
        finally
        {
            await reader.CompleteAsync();
            try { socket.Shutdown(SocketShutdown.Both); } catch (SocketException) { } catch (ObjectDisposedException) { }
        }
    }

    /// <summary>HTTP/1.1 keeps connections open unless told otherwise; HTTP/1.0 closes unless asked to keep alive.</summary>
    internal static bool ShouldKeepAlive(HttpRequest request)
    {
        var connection = request.Headers["Connection"];
        return request.Protocol == "HTTP/1.1"
            ? !string.Equals(connection, "close", StringComparison.OrdinalIgnoreCase)
            : string.Equals(connection, "keep-alive", StringComparison.OrdinalIgnoreCase);
    }
}

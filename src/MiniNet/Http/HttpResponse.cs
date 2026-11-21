using System.Text;
using System.Text.Json;

namespace MiniNet.Http;

/// <summary>
/// A buffered response. Handlers write into <see cref="Body"/>, and the server sends the whole
/// response once the pipeline finishes. Buffering keeps the design simple and lets the server always
/// send an exact Content-Length; the trade-off is that very large responses are held in memory.
/// </summary>
public sealed class HttpResponse
{
    public int StatusCode { get; set; } = 200;

    public HttpHeaders Headers { get; } = new();

    public MemoryStream Body { get; } = new();

    public string? ContentType
    {
        get => Headers["Content-Type"];
        set => Headers["Content-Type"] = value;
    }

    /// <summary>True once a handler has written something, so later middleware can tell the response was produced.</summary>
    public bool HasBody => Body.Length > 0;

    public Task WriteAsync(string text, CancellationToken cancellationToken = default)
    {
        ContentType ??= "text/plain; charset=utf-8";
        var bytes = Encoding.UTF8.GetBytes(text);
        return Body.WriteAsync(bytes, cancellationToken).AsTask();
    }

    public Task WriteJsonAsync<T>(T value, JsonSerializerOptions? options = null, CancellationToken cancellationToken = default) =>
        WriteJsonAsync(value, typeof(T), options, cancellationToken);

    public Task WriteJsonAsync(object? value, Type type, JsonSerializerOptions? options = null, CancellationToken cancellationToken = default)
    {
        ContentType = "application/json; charset=utf-8";
        return JsonSerializer.SerializeAsync(Body, value, type, options ?? MiniJson.Options, cancellationToken);
    }

    /// <summary>Clears the status, headers and body, for example when an error handler replaces a half-written response.</summary>
    public void Clear()
    {
        StatusCode = 200;
        foreach (var name in Headers.Select(h => h.Key).ToList()) Headers.Remove(name);
        Body.SetLength(0);
    }
}

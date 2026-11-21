using System.Text;
using System.Text.Json;

namespace MiniNet.Http;

public sealed class HttpRequest
{
    private IReadOnlyDictionary<string, string>? _query;

    public HttpRequest(string method, string path, string queryString, string protocol, HttpHeaders headers, byte[] body)
    {
        Method = method;
        Path = path;
        QueryString = queryString;
        Protocol = protocol;
        Headers = headers;
        Body = body;
    }

    /// <summary>Upper-case HTTP method, for example "GET".</summary>
    public string Method { get; }

    /// <summary>
    /// The path the current pipeline branch sees. <c>Map("/api", ...)</c> moves "/api" into
    /// <see cref="PathBase"/> while the branch runs, the same way ASP.NET Core does.
    /// </summary>
    public string Path { get; set; }

    public string PathBase { get; set; } = string.Empty;

    /// <summary>Raw query string without the leading '?'.</summary>
    public string QueryString { get; }

    public string Protocol { get; }

    public HttpHeaders Headers { get; }

    public byte[] Body { get; }

    public string? ContentType => Headers["Content-Type"];

    /// <summary>Values captured by the matched route template, such as <c>id</c> in <c>/users/{id}</c>.</summary>
    public Dictionary<string, string> RouteValues { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, string> Query => _query ??= ParseQuery(QueryString);

    public string ReadBodyAsString() => Encoding.UTF8.GetString(Body);

    public T? ReadJson<T>(JsonSerializerOptions? options = null) =>
        Body.Length == 0 ? default : JsonSerializer.Deserialize<T>(Body, options ?? MiniJson.Options);

    /// <summary>
    /// Builds a request without going through the network, for tests and for <c>MiniApp.SendAsync</c>.
    /// A non-null <paramref name="body"/> is sent as UTF-8 with Content-Type application/json unless a header says otherwise.
    /// </summary>
    public static HttpRequest Create(string method, string target, string? body = null, params (string Name, string Value)[] headers)
    {
        var queryStart = target.IndexOf('?');
        var path = Uri.UnescapeDataString(queryStart < 0 ? target : target[..queryStart]);
        var query = queryStart < 0 ? string.Empty : target[(queryStart + 1)..];

        var headerCollection = new HttpHeaders { ["Host"] = "localhost" };
        foreach (var (name, value) in headers) headerCollection[name] = value;

        var bytes = body is null ? [] : Encoding.UTF8.GetBytes(body);
        if (body is not null && !headerCollection.ContainsKey("Content-Type")) headerCollection["Content-Type"] = "application/json";
        if (bytes.Length > 0) headerCollection["Content-Length"] = bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);

        return new HttpRequest(method.ToUpperInvariant(), path, query, "HTTP/1.1", headerCollection, bytes);
    }

    internal static IReadOnlyDictionary<string, string> ParseQuery(string queryString)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(queryString)) return result;

        foreach (var pair in queryString.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var key = Decode(separator < 0 ? pair : pair[..separator]);
            var value = separator < 0 ? string.Empty : Decode(pair[(separator + 1)..]);
            if (key.Length == 0) continue;

            // Repeated keys (?tag=a&tag=b) are joined, mirroring how headers are combined.
            result[key] = result.TryGetValue(key, out var existing) ? existing + "," + value : value;
        }

        return result;
    }

    private static string Decode(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));
}

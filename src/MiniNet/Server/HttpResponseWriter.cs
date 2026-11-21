using System.Globalization;
using System.Text;
using MiniNet.Http;

namespace MiniNet.Server;

/// <summary>Serializes an <see cref="HttpResponse"/> into HTTP/1.1 wire format.</summary>
public static class HttpResponseWriter
{
    // Headers the server controls itself. A handler setting them could corrupt the framing of the connection.
    private static readonly HashSet<string> ServerManagedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Length", "Connection", "Date", "Transfer-Encoding",
    };

    public static byte[] Serialize(HttpResponse response, bool keepAlive, bool isHeadRequest)
    {
        var allowsBody = StatusCodes.AllowsBody(response.StatusCode);
        var bodyLength = allowsBody ? response.Body.Length : 0;

        var head = new StringBuilder(256);
        head.Append("HTTP/1.1 ")
            .Append(response.StatusCode.ToString(CultureInfo.InvariantCulture)).Append(' ')
            .Append(StatusCodes.ReasonPhrase(response.StatusCode)).Append("\r\n");

        head.Append("Date: ").Append(DateTimeOffset.UtcNow.ToString("r", CultureInfo.InvariantCulture)).Append("\r\n");
        head.Append("Server: MiniNet\r\n");
        if (allowsBody)
            head.Append("Content-Length: ").Append(bodyLength.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        head.Append("Connection: ").Append(keepAlive ? "keep-alive" : "close").Append("\r\n");

        foreach (var (name, value) in response.Headers)
        {
            if (ServerManagedHeaders.Contains(name)) continue;
            // Refuse CR/LF in header values: otherwise a value taken from user input could inject extra headers.
            if (value.AsSpan().IndexOfAny('\r', '\n') >= 0)
                throw new InvalidOperationException($"Header '{name}' contains a line break.");
            head.Append(name).Append(": ").Append(value).Append("\r\n");
        }

        head.Append("\r\n");

        var headBytes = Encoding.Latin1.GetBytes(head.ToString());
        // HEAD responses carry the same headers as GET, including Content-Length, but no body (RFC 9110 §9.3.2).
        if (isHeadRequest || bodyLength == 0) return headBytes;

        var result = new byte[headBytes.Length + bodyLength];
        headBytes.CopyTo(result, 0);
        response.Body.GetBuffer().AsSpan(0, (int)bodyLength).CopyTo(result.AsSpan(headBytes.Length));
        return result;
    }

    public static byte[] SerializeError(int statusCode, string message)
    {
        var response = new HttpResponse { StatusCode = statusCode, ContentType = "text/plain; charset=utf-8" };
        response.Body.Write(Encoding.UTF8.GetBytes(message));
        return Serialize(response, keepAlive: false, isHeadRequest: false);
    }
}

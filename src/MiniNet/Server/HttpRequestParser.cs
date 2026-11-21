using System.Buffers;
using System.Text;
using MiniNet.Http;

namespace MiniNet.Server;

public enum ParseStatus
{
    /// <summary>Not enough bytes yet. The caller should read more from the socket and try again.</summary>
    Incomplete,

    /// <summary>A full request was parsed and its bytes removed from the buffer.</summary>
    Complete,

    /// <summary>The request is invalid. <see cref="ParseResult.ErrorStatusCode"/> says which error to send.</summary>
    Error,
}

public readonly record struct ParseResult(ParseStatus Status, HttpRequest? Request = null, int ErrorStatusCode = 0, string? ErrorMessage = null)
{
    public static readonly ParseResult Incomplete = new(ParseStatus.Incomplete);

    public static ParseResult Error(int statusCode, string message) => new(ParseStatus.Error, null, statusCode, message);
}

/// <summary>
/// Parses HTTP/1.1 requests (RFC 9112) from a <see cref="ReadOnlySequence{T}"/>.
///
/// The input is a sequence rather than an array because TCP gives no guarantee about how bytes are
/// split: one request can arrive across many reads, and one read can hold several pipelined
/// requests. The parser never assumes a line is in one segment. When the bytes so far are only part
/// of a request it returns <see cref="ParseStatus.Incomplete"/> and leaves the buffer untouched, so the
/// caller can append the next read and parse again.
/// </summary>
public static class HttpRequestParser
{
    private static ReadOnlySpan<byte> CrLf => "\r\n"u8;

    public static ParseResult TryParse(ref ReadOnlySequence<byte> buffer, ServerLimits limits)
    {
        var reader = new SequenceReader<byte>(buffer);

        // 1. Request line: METHOD SP TARGET SP VERSION CRLF
        if (!reader.TryReadTo(out ReadOnlySequence<byte> requestLine, CrLf, advancePastDelimiter: true))
        {
            return reader.Remaining > limits.MaxRequestLineBytes
                ? ParseResult.Error(414, "Request line is too long.")
                : ParseResult.Incomplete;
        }

        if (requestLine.Length > limits.MaxRequestLineBytes)
            return ParseResult.Error(414, "Request line is too long.");

        var lineResult = ParseRequestLine(requestLine, out var method, out var path, out var query, out var protocol);
        if (lineResult is not null) return lineResult.Value;

        // 2. Header lines, ending with an empty line.
        var headers = new HttpHeaders();
        var headerSectionStart = reader.Consumed;
        while (true)
        {
            if (!reader.TryReadTo(out ReadOnlySequence<byte> headerLine, CrLf, advancePastDelimiter: true))
            {
                var buffered = reader.Consumed - headerSectionStart + reader.Remaining;
                return buffered > limits.MaxHeaderBytes
                    ? ParseResult.Error(431, "Request header section is too large.")
                    : ParseResult.Incomplete;
            }

            if (reader.Consumed - headerSectionStart > limits.MaxHeaderBytes)
                return ParseResult.Error(431, "Request header section is too large.");

            if (headerLine.Length == 0) break; // blank line: headers are done

            var headerError = ParseHeaderLine(headerLine, headers);
            if (headerError is not null) return headerError.Value;

            if (headers.Count > limits.MaxHeaderCount)
                return ParseResult.Error(431, "Too many request headers.");
        }

        // 3. Rules that need all headers.
        if (protocol == "HTTP/1.1" && !headers.ContainsKey("Host"))
            return ParseResult.Error(400, "HTTP/1.1 requests must include a Host header.");

        if (headers.ContainsKey("Transfer-Encoding"))
            return ParseResult.Error(501, "Transfer-Encoding (chunked request bodies) is not supported. Send Content-Length instead.");

        long contentLength = 0;
        if (headers.TryGetValue("Content-Length", out var contentLengthValue))
        {
            // Two different Content-Length values are a classic request-smuggling trick (RFC 9112 §6.3), so reject them.
            var values = contentLengthValue.Split(',', StringSplitOptions.TrimEntries);
            if (values.Distinct().Count() != 1 || !long.TryParse(values[0], out contentLength) || contentLength < 0)
                return ParseResult.Error(400, "Invalid Content-Length header.");
        }

        if (contentLength > limits.MaxBodyBytes)
            return ParseResult.Error(413, "Request body is too large.");

        // 4. Body. The headers are complete but the body may still be arriving.
        if (reader.Remaining < contentLength) return ParseResult.Incomplete;

        var body = contentLength == 0 ? [] : reader.UnreadSequence.Slice(0, contentLength).ToArray();
        reader.Advance(contentLength);

        buffer = buffer.Slice(reader.Position);
        var request = new HttpRequest(method, path, query, protocol, headers, body);
        return new ParseResult(ParseStatus.Complete, request);
    }

    private static ParseResult? ParseRequestLine(ReadOnlySequence<byte> line, out string method, out string path, out string query, out string protocol)
    {
        method = path = query = protocol = string.Empty;

        var text = Encoding.ASCII.GetString(line);
        var parts = text.Split(' ');
        if (parts.Length != 3 || parts.Any(p => p.Length == 0))
            return ParseResult.Error(400, "Malformed request line.");

        method = parts[0];
        if (!method.All(IsTokenChar))
            return ParseResult.Error(400, "Invalid HTTP method.");

        protocol = parts[2];
        if (protocol is not ("HTTP/1.1" or "HTTP/1.0"))
        {
            return protocol.StartsWith("HTTP/", StringComparison.Ordinal)
                ? ParseResult.Error(505, "Only HTTP/1.0 and HTTP/1.1 are supported.")
                : ParseResult.Error(400, "Malformed HTTP version.");
        }

        var target = parts[1];
        if (target[0] != '/')
            return ParseResult.Error(400, "Only origin-form request targets (starting with '/') are supported.");

        var queryStart = target.IndexOf('?');
        var rawPath = queryStart < 0 ? target : target[..queryStart];
        query = queryStart < 0 ? string.Empty : target[(queryStart + 1)..];

        try
        {
            path = Uri.UnescapeDataString(rawPath);
        }
        catch (UriFormatException)
        {
            return ParseResult.Error(400, "Invalid percent-encoding in path.");
        }

        if (path.Contains('\0'))
            return ParseResult.Error(400, "Path contains a null character.");

        return null;
    }

    private static ParseResult? ParseHeaderLine(ReadOnlySequence<byte> line, HttpHeaders headers)
    {
        var text = Encoding.Latin1.GetString(line);

        // Obsolete line folding (a header continued on a line starting with whitespace) is not allowed (RFC 9112 §5.2).
        if (text[0] is ' ' or '\t')
            return ParseResult.Error(400, "Header line folding is not supported.");

        var colon = text.IndexOf(':');
        if (colon <= 0)
            return ParseResult.Error(400, "Malformed header line.");

        var name = text[..colon];
        // No whitespace is allowed between the name and the colon (RFC 9112 §5.1).
        if (!name.All(IsTokenChar))
            return ParseResult.Error(400, $"Invalid header name '{name}'.");

        var value = text[(colon + 1)..].Trim(' ', '\t');
        headers.Append(name, value);
        return null;
    }

    // "token" characters from RFC 9110 §5.6.2
    private static bool IsTokenChar(char c) =>
        char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c);
}

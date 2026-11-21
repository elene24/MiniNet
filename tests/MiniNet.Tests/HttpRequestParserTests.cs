using System.Buffers;
using System.Text;
using MiniNet.Server;

namespace MiniNet.Tests;

public class HttpRequestParserTests
{
    private static readonly ServerLimits Limits = new();

    private const string PostRequest =
        "POST /orders?expedite=true HTTP/1.1\r\n" +
        "Host: localhost\r\n" +
        "Content-Type: application/json\r\n" +
        "Content-Length: 13\r\n" +
        "\r\n" +
        "{\"total\":42}\n";

    [Fact]
    public void Parses_a_simple_GET()
    {
        var buffer = Sequences.Single("GET /users/7?sort=name&dir=asc HTTP/1.1\r\nHost: example.com\r\nX-Trace: abc\r\n\r\n");

        var result = HttpRequestParser.TryParse(ref buffer, Limits);

        Assert.Equal(ParseStatus.Complete, result.Status);
        var request = result.Request!;
        Assert.Equal("GET", request.Method);
        Assert.Equal("/users/7", request.Path);
        Assert.Equal("name", request.Query["sort"]);
        Assert.Equal("asc", request.Query["dir"]);
        Assert.Equal("abc", request.Headers["x-trace"]); // header names are case-insensitive
        Assert.Empty(request.Body);
        Assert.Equal(0, buffer.Length); // the parsed request was consumed
    }

    [Fact]
    public void Reads_the_body_using_Content_Length()
    {
        var buffer = Sequences.Single(PostRequest);

        var result = HttpRequestParser.TryParse(ref buffer, Limits);

        Assert.Equal(ParseStatus.Complete, result.Status);
        Assert.Equal("{\"total\":42}\n", result.Request!.ReadBodyAsString());
    }

    [Fact]
    public void Returns_incomplete_and_leaves_buffer_untouched_when_headers_are_cut_off()
    {
        var original = Sequences.Single("GET / HTTP/1.1\r\nHost: loc");
        var buffer = original;

        var result = HttpRequestParser.TryParse(ref buffer, Limits);

        Assert.Equal(ParseStatus.Incomplete, result.Status);
        Assert.Equal(original.Length, buffer.Length);
    }

    [Fact]
    public void Returns_incomplete_until_the_whole_body_has_arrived()
    {
        var buffer = Sequences.Single(PostRequest[..^5]);

        Assert.Equal(ParseStatus.Incomplete, HttpRequestParser.TryParse(ref buffer, Limits).Status);
    }

    [Fact]
    public void Parses_correctly_wherever_TCP_splits_the_bytes()
    {
        // TCP is a byte stream: a request can arrive in pieces split at any position,
        // including in the middle of "\r\n". Try every possible split point.
        var bytes = Encoding.ASCII.GetBytes(PostRequest);
        for (var cut = 1; cut < bytes.Length; cut++)
        {
            var buffer = Sequences.Split(bytes, cut);
            var result = HttpRequestParser.TryParse(ref buffer, Limits);

            Assert.Equal(ParseStatus.Complete, result.Status);
            Assert.Equal("/orders", result.Request!.Path);
            Assert.Equal("{\"total\":42}\n", result.Request.ReadBodyAsString());
        }
    }

    [Fact]
    public void Parses_a_request_delivered_one_byte_per_segment()
    {
        var bytes = Encoding.ASCII.GetBytes(PostRequest);
        var buffer = Sequences.Split(bytes, Enumerable.Range(1, bytes.Length - 1).ToArray());

        var result = HttpRequestParser.TryParse(ref buffer, Limits);

        Assert.Equal(ParseStatus.Complete, result.Status);
        Assert.Equal("application/json", result.Request!.ContentType);
    }

    [Fact]
    public void Parses_pipelined_requests_one_at_a_time()
    {
        var buffer = Sequences.Single("GET /first HTTP/1.1\r\nHost: a\r\n\r\nGET /second HTTP/1.1\r\nHost: a\r\n\r\n");

        var first = HttpRequestParser.TryParse(ref buffer, Limits);
        var second = HttpRequestParser.TryParse(ref buffer, Limits);

        Assert.Equal("/first", first.Request!.Path);
        Assert.Equal("/second", second.Request!.Path);
        Assert.Equal(0, buffer.Length);
    }

    [Fact]
    public void Decodes_percent_encoded_paths_and_query_values()
    {
        var buffer = Sequences.Single("GET /files/my%20report.pdf?q=hello+world&name=Elene%20M HTTP/1.1\r\nHost: a\r\n\r\n");

        var request = HttpRequestParser.TryParse(ref buffer, Limits).Request!;

        Assert.Equal("/files/my report.pdf", request.Path);
        Assert.Equal("hello world", request.Query["q"]);
        Assert.Equal("Elene M", request.Query["name"]);
    }

    [Fact]
    public void Combines_repeated_headers()
    {
        var buffer = Sequences.Single("GET / HTTP/1.1\r\nHost: a\r\nAccept: text/html\r\nAccept: application/json\r\n\r\n");

        var request = HttpRequestParser.TryParse(ref buffer, Limits).Request!;

        Assert.Equal("text/html, application/json", request.Headers["Accept"]);
    }

    [Theory]
    [InlineData("NONSENSE\r\n\r\n", 400)]
    [InlineData("GET /\r\n\r\n", 400)]
    [InlineData("GET / HTTP/2.0\r\nHost: a\r\n\r\n", 505)]
    [InlineData("GET http://example.com/ HTTP/1.1\r\nHost: a\r\n\r\n", 400)]
    [InlineData("G(T / HTTP/1.1\r\nHost: a\r\n\r\n", 400)]
    [InlineData("GET / HTTP/1.1\r\n\r\n", 400)] // HTTP/1.1 requires Host
    [InlineData("GET / HTTP/1.1\r\nHost : a\r\n\r\n", 400)] // space before colon
    [InlineData("GET / HTTP/1.1\r\nHost: a\r\nX-Long: one\r\n two\r\n\r\n", 400)] // obsolete line folding
    [InlineData("POST / HTTP/1.1\r\nHost: a\r\nContent-Length: abc\r\n\r\n", 400)]
    [InlineData("POST / HTTP/1.1\r\nHost: a\r\nContent-Length: -1\r\n\r\n", 400)]
    [InlineData("POST / HTTP/1.1\r\nHost: a\r\nContent-Length: 5\r\nContent-Length: 6\r\n\r\nhello!", 400)] // request smuggling attempt
    [InlineData("POST / HTTP/1.1\r\nHost: a\r\nTransfer-Encoding: chunked\r\n\r\n0\r\n\r\n", 501)]
    public void Rejects_invalid_requests_with_the_right_status(string raw, int expectedStatus)
    {
        var buffer = Sequences.Single(raw);

        var result = HttpRequestParser.TryParse(ref buffer, Limits);

        Assert.Equal(ParseStatus.Error, result.Status);
        Assert.Equal(expectedStatus, result.ErrorStatusCode);
    }

    [Fact]
    public void Accepts_identical_repeated_Content_Length_headers()
    {
        var buffer = Sequences.Single("POST / HTTP/1.1\r\nHost: a\r\nContent-Length: 2\r\nContent-Length: 2\r\n\r\nhi");

        Assert.Equal(ParseStatus.Complete, HttpRequestParser.TryParse(ref buffer, Limits).Status);
    }

    [Fact]
    public void Rejects_a_request_line_that_is_too_long_even_before_it_ends()
    {
        var limits = new ServerLimits { MaxRequestLineBytes = 64 };
        var buffer = Sequences.Single("GET /" + new string('a', 100)); // no CRLF yet

        var result = HttpRequestParser.TryParse(ref buffer, limits);

        Assert.Equal(414, result.ErrorStatusCode);
    }

    [Fact]
    public void Rejects_oversized_header_sections()
    {
        var limits = new ServerLimits { MaxHeaderBytes = 100 };
        var buffer = Sequences.Single($"GET / HTTP/1.1\r\nHost: a\r\nX-Big: {new string('x', 200)}\r\n\r\n");

        Assert.Equal(431, HttpRequestParser.TryParse(ref buffer, limits).ErrorStatusCode);
    }

    [Fact]
    public void Rejects_too_many_headers()
    {
        var limits = new ServerLimits { MaxHeaderCount = 3 };
        var headers = string.Concat(Enumerable.Range(0, 5).Select(i => $"X-H{i}: {i}\r\n"));
        var buffer = Sequences.Single($"GET / HTTP/1.1\r\nHost: a\r\n{headers}\r\n");

        Assert.Equal(431, HttpRequestParser.TryParse(ref buffer, limits).ErrorStatusCode);
    }

    [Fact]
    public void Rejects_bodies_over_the_limit_before_reading_them()
    {
        var limits = new ServerLimits { MaxBodyBytes = 10 };
        var buffer = Sequences.Single("POST / HTTP/1.1\r\nHost: a\r\nContent-Length: 1000000\r\n\r\n");

        Assert.Equal(413, HttpRequestParser.TryParse(ref buffer, limits).ErrorStatusCode);
    }

    [Fact]
    public void Understands_HTTP_1_0()
    {
        var buffer = Sequences.Single("GET /legacy HTTP/1.0\r\n\r\n"); // Host is optional in 1.0

        var result = HttpRequestParser.TryParse(ref buffer, Limits);

        Assert.Equal("HTTP/1.0", result.Request!.Protocol);
        Assert.False(MiniServer.ShouldKeepAlive(result.Request));
    }
}

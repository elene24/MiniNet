using System.Net;
using System.Text;
using MiniNet.Endpoints;
using MiniNet.Hosting;

namespace MiniNet.Tests;

/// <summary>End-to-end tests over real TCP connections on a random free port.</summary>
public class ServerIntegrationTests : IAsyncLifetime
{
    private MiniApp _app = null!;
    private readonly TaskCompletionSource _slowRequestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task InitializeAsync()
    {
        _app = TestApp.Create(b =>
        {
            // Long enough for the one-byte-at-a-time test: on Windows, Task.Delay(1) really waits about 15 ms,
            // so sending ~70 bytes takes around a second. Still short enough for the 408 test to finish quickly.
            b.Server.Limits.RequestTimeout = TimeSpan.FromSeconds(3);
            b.Server.ShutdownTimeout = TimeSpan.FromSeconds(5);
        });
        _app.MapGet("/hello/{name}", (string name) => $"Hello, {name}");
        _app.MapPost("/echo", (EchoMessage body) => body);
        _app.MapGet("/slow", async () =>
        {
            _slowRequestStarted.TrySetResult();
            await Task.Delay(400);
            return "finally";
        });
        await _app.StartAsync();
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    public sealed record EchoMessage(string Message);

    private static HttpClient Client() => new(new SocketsHttpHandler { UseProxy = false });

    [Fact]
    public async Task Serves_real_HttpClient_requests()
    {
        using var client = Client();

        var text = await client.GetStringAsync($"{_app.Url}/hello/Elene");
        var echo = await client.PostAsync($"{_app.Url}/echo", new StringContent("""{"message":"hi"}""", Encoding.UTF8, "application/json"));

        Assert.Equal("Hello, Elene", text);
        Assert.Equal(HttpStatusCode.OK, echo.StatusCode);
        Assert.Equal("""{"message":"hi"}""", await echo.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Handles_a_request_sent_one_byte_at_a_time()
    {
        var request = Encoding.ASCII.GetBytes("GET /hello/slow-sender HTTP/1.1\r\nHost: test\r\nConnection: close\r\n\r\n");

        var raw = await RawHttp.SendAsync(_app.Port, request.Select(b => new[] { b }), TimeSpan.FromMilliseconds(1));

        Assert.StartsWith("HTTP/1.1 200 OK", raw);
        Assert.EndsWith("Hello, slow-sender", raw);
    }

    [Fact]
    public async Task Keeps_the_connection_open_for_several_requests_and_answers_pipelined_requests_in_order()
    {
        var raw = await RawHttp.SendAsync(_app.Port,
            "GET /hello/one HTTP/1.1\r\nHost: t\r\n\r\n" +
            "GET /hello/two HTTP/1.1\r\nHost: t\r\n\r\n" +
            "GET /hello/three HTTP/1.1\r\nHost: t\r\nConnection: close\r\n\r\n");

        Assert.Equal(3, RawHttp.CountResponses(raw));
        Assert.True(raw.IndexOf("Hello, one", StringComparison.Ordinal) < raw.IndexOf("Hello, two", StringComparison.Ordinal));
        Assert.True(raw.IndexOf("Hello, two", StringComparison.Ordinal) < raw.IndexOf("Hello, three", StringComparison.Ordinal));
        Assert.Contains("Connection: close", raw);
    }

    [Fact]
    public async Task HTTP_1_0_connections_close_after_one_response()
    {
        var raw = await RawHttp.SendAsync(_app.Port, "GET /hello/old HTTP/1.0\r\n\r\nGET /hello/ignored HTTP/1.0\r\n\r\n");

        Assert.Equal(1, RawHttp.CountResponses(raw));
    }

    [Fact]
    public async Task A_malformed_request_gets_400_and_the_connection_is_closed()
    {
        var raw = await RawHttp.SendAsync(_app.Port, "THIS IS NOT HTTP\r\n\r\n");

        Assert.StartsWith("HTTP/1.1 400 Bad Request", raw);
        Assert.Contains("Connection: close", raw);
    }

    [Fact]
    public async Task A_client_that_stops_mid_request_gets_408()
    {
        // Sends half the headers, then nothing: the classic slowloris pattern.
        var raw = await RawHttp.SendAsync(_app.Port, [Encoding.ASCII.GetBytes("GET /hello/x HTTP/1.1\r\nHost: t\r\n")]);

        Assert.StartsWith("HTTP/1.1 408 Request Timeout", raw);
    }

    [Fact]
    public async Task HEAD_responses_have_headers_but_no_body()
    {
        var raw = await RawHttp.SendAsync(_app.Port, "HEAD /hello/head HTTP/1.1\r\nHost: t\r\nConnection: close\r\n\r\n");

        Assert.Contains("Content-Length: 11", raw); // length of "Hello, head"
        Assert.EndsWith("\r\n\r\n", raw);
    }

    [Fact]
    public async Task Shutdown_lets_in_flight_requests_finish()
    {
        using var client = Client();
        var pending = client.GetStringAsync($"{_app.Url}/slow");
        await _slowRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(10)); // wait until the request is really being handled

        await _app.StopAsync();

        Assert.Equal("finally", await pending);
    }

    [Fact]
    public async Task Handles_many_concurrent_connections()
    {
        using var client = Client();

        var results = await Task.WhenAll(Enumerable.Range(0, 100).Select(i => client.GetStringAsync($"{_app.Url}/hello/{i}")));

        Assert.Equal(Enumerable.Range(0, 100).Select(i => $"Hello, {i}"), results);
    }
}

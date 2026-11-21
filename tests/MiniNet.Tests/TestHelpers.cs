using System.Buffers;
using System.Net.Sockets;
using System.Text;
using MiniNet.DependencyInjection;
using MiniNet.Hosting;
using MiniNet.Http;
using MiniNet.Logging;

namespace MiniNet.Tests;

/// <summary>A clock tests can move forward by hand, so time-based behaviour is tested without sleeping.</summary>
public sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

public static class TestApp
{
    /// <summary>An app on port 0 (any free port) that logs to memory instead of the console.</summary>
    public static MiniApp Create(Action<MiniAppBuilder>? configure = null)
    {
        var builder = MiniApp.CreateBuilder();
        builder.Server.Port = 0;
        builder.Services.AddSingleton<ILogger>(new InMemoryLogger());
        configure?.Invoke(builder);
        return builder.Build();
    }

    public static string BodyText(this HttpResponse response) => Encoding.UTF8.GetString(response.Body.ToArray());

    public static Task<HttpResponse> GetAsync(this MiniApp app, string target) => app.SendAsync(HttpRequest.Create("GET", target));

    public static Task<HttpResponse> PostJsonAsync(this MiniApp app, string target, string json) =>
        app.SendAsync(HttpRequest.Create("POST", target, json));
}

public static class Sequences
{
    public static ReadOnlySequence<byte> Single(string text) => new(Encoding.ASCII.GetBytes(text));

    /// <summary>Builds a multi-segment sequence, the way bytes look after several separate TCP reads.</summary>
    public static ReadOnlySequence<byte> Split(byte[] data, params int[] cutPoints)
    {
        var bounds = cutPoints.Where(c => c > 0 && c < data.Length).Distinct().Order().Prepend(0).Append(data.Length).ToArray();
        Segment? first = null, last = null;
        for (var i = 0; i < bounds.Length - 1; i++)
        {
            var chunk = data.AsMemory(bounds[i], bounds[i + 1] - bounds[i]);
            if (first is null) first = last = new Segment(chunk, 0);
            else last = last!.Append(chunk);
        }

        return new ReadOnlySequence<byte>(first!, 0, last!, last!.Memory.Length);
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory, long runningIndex)
        {
            Memory = memory;
            RunningIndex = runningIndex;
        }

        public Segment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new Segment(memory, RunningIndex + Memory.Length);
            Next = next;
            return next;
        }
    }
}

public static class RawHttp
{
    /// <summary>
    /// Sends raw bytes over a real TCP connection and returns everything the server sends back until it
    /// closes the connection. Lets tests control exactly how requests are split across writes.
    /// </summary>
    public static async Task<string> SendAsync(int port, IEnumerable<byte[]> chunks, TimeSpan? delayBetweenChunks = null, TimeSpan? timeout = null)
    {
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", port);
        client.NoDelay = true;
        var stream = client.GetStream();

        foreach (var chunk in chunks)
        {
            await stream.WriteAsync(chunk);
            await stream.FlushAsync();
            if (delayBetweenChunks is { } delay) await Task.Delay(delay);
        }

        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        var received = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = await stream.ReadAsync(buffer, cts.Token)) > 0) received.Write(buffer, 0, read);
        return Encoding.UTF8.GetString(received.ToArray());
    }

    public static Task<string> SendAsync(int port, string request, TimeSpan? timeout = null) =>
        SendAsync(port, [Encoding.ASCII.GetBytes(request)], timeout: timeout);

    public static int CountResponses(string raw) => raw.Split("HTTP/1.1 ").Length - 1;
}

using System.Buffers;
using System.Text;
using BenchmarkDotNet.Attributes;
using MiniNet.Server;

namespace MiniNet.Benchmarks;

/// <summary>How fast is request parsing, and what does it allocate? Includes a request split into many TCP reads.</summary>
[MemoryDiagnoser]
public class ParserBenchmarks
{
    private static readonly ServerLimits Limits = new();
    private ReadOnlySequence<byte> _get;
    private ReadOnlySequence<byte> _post;
    private ReadOnlySequence<byte> _fragmented;

    [GlobalSetup]
    public void Setup()
    {
        const string get =
            "GET /api/v1/doctors/42/slots?date=2026-12-01 HTTP/1.1\r\n" +
            "Host: localhost:8080\r\n" +
            "User-Agent: Mozilla/5.0 (X11; Linux x86_64)\r\n" +
            "Accept: application/json\r\n" +
            "Accept-Encoding: gzip, deflate, br\r\n" +
            "Connection: keep-alive\r\n\r\n";
        var body = """{"patientName":"Elene Margalitadze","startsAt":"2026-12-01T10:00:00+04:00"}""";
        var post =
            "POST /doctors/1/appointments HTTP/1.1\r\n" +
            "Host: localhost:8080\r\n" +
            "Content-Type: application/json\r\n" +
            $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n" + body;

        _get = new ReadOnlySequence<byte>(Encoding.ASCII.GetBytes(get));
        _post = new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes(post));
        var bytes = Encoding.ASCII.GetBytes(get);
        _fragmented = SegmentedSequence.Create(bytes, chunkSize: 16);
    }

    [Benchmark(Baseline = true)]
    public object? ParseGet()
    {
        var buffer = _get;
        return HttpRequestParser.TryParse(ref buffer, Limits).Request;
    }

    [Benchmark]
    public object? ParsePostWithJsonBody()
    {
        var buffer = _post;
        return HttpRequestParser.TryParse(ref buffer, Limits).Request;
    }

    [Benchmark]
    public object? ParseGetSplitInto16ByteSegments()
    {
        var buffer = _fragmented;
        return HttpRequestParser.TryParse(ref buffer, Limits).Request;
    }
}

internal static class SegmentedSequence
{
    public static ReadOnlySequence<byte> Create(byte[] data, int chunkSize)
    {
        Segment? first = null, last = null;
        for (var offset = 0; offset < data.Length; offset += chunkSize)
        {
            var chunk = data.AsMemory(offset, Math.Min(chunkSize, data.Length - offset));
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

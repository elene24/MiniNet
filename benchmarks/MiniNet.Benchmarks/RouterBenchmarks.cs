using BenchmarkDotNet.Attributes;
using MiniNet.Routing;

namespace MiniNet.Benchmarks;

/// <summary>
/// The segment tree against the obvious alternative: check every route in a list, one by one.
/// With few routes the difference is small; it grows with the number of routes, because the list
/// scan is O(routes) and the tree is O(path segments).
/// </summary>
[MemoryDiagnoser]
public class RouterBenchmarks
{
    private Router _tree = null!;
    private List<Endpoint> _endpoints = null!;
    private string _lastRoutePath = null!;

    [Params(10, 100, 500)]
    public int RouteCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _endpoints = Enumerable.Range(0, RouteCount)
            .Select(i => new Endpoint("GET", RouteTemplate.Parse($"/api/resource{i}/{{id:int}}/items/{{itemId}}"), _ => Task.CompletedTask))
            .ToList();
        _tree = new Router(_endpoints);
        _lastRoutePath = $"/api/resource{RouteCount - 1}/42/items/abc"; // worst case for the list scan
    }

    [Benchmark(Baseline = true)]
    public Endpoint? LinearScan()
    {
        var segments = _lastRoutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var endpoint in _endpoints)
        {
            if (endpoint.HttpMethod == "GET" && SegmentsMatch(endpoint.Template, segments)) return endpoint;
        }

        return null;
    }

    [Benchmark]
    public Endpoint? SegmentTree() => _tree.Match(_lastRoutePath, "GET").Endpoint;

    private static bool SegmentsMatch(RouteTemplate template, string[] segments)
    {
        if (template.Segments.Count != segments.Length) return false;
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = template.Segments[i];
            var ok = segment.Kind switch
            {
                SegmentKind.Literal => string.Equals(segment.Value, segments[i], StringComparison.OrdinalIgnoreCase),
                _ => segment.Constraint?.Match(segments[i]) ?? true,
            };
            if (!ok) return false;
        }

        return true;
    }
}

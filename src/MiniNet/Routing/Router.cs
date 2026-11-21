namespace MiniNet.Routing;

public sealed record RouteMatch(Endpoint? Endpoint, IReadOnlyDictionary<string, string> Values, IReadOnlyCollection<string> AllowedMethods)
{
    public bool PathMatched => Endpoint is not null || AllowedMethods.Count > 0;
}

/// <summary>
/// Matches request paths against registered endpoints using a tree of path segments.
///
/// Checking every route in a list costs O(number of routes) per request. In the tree, each level
/// is one path segment, so matching costs about O(number of segments) whatever the number of routes.
///
/// Priority at each level, most specific first: literal ("/users/me"), then constrained parameter
/// ("{id:int}"), then plain parameter ("{name}"), then catch-all ("{*path}"). If a more specific
/// branch fails further down, matching backtracks and tries the next one.
/// </summary>
public sealed class Router
{
    private readonly Node _root = new();

    public Router(IEnumerable<Endpoint> endpoints)
    {
        foreach (var endpoint in endpoints) Add(endpoint);
        _root.Sort();
    }

    public RouteMatch Match(string path, string method)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var captured = new List<string>();
        var allowed = new SortedSet<string>(StringComparer.Ordinal);

        var found = Search(_root, segments, 0, method, captured, allowed);
        if (found is null) return new RouteMatch(null, EmptyValues, allowed);

        var (endpoint, values) = found.Value;
        var dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var names = endpoint.Template.ParameterNames;
        for (var i = 0; i < values.Count && i < names.Count; i++) dictionary[names[i]] = values[i];
        return new RouteMatch(endpoint, dictionary, allowed);
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyValues = new Dictionary<string, string>();

    private static (Endpoint, List<string>)? Search(Node node, string[] segments, int index, string method, List<string> captured, SortedSet<string> allowed)
    {
        if (index == segments.Length)
        {
            if (TryEndpoint(node, method, captured, allowed) is { } hit) return hit;

            // "{*path}" also matches when nothing is left, so "/files" matches "/files/{*path}" with path = "".
            if (node.CatchAll is not null)
            {
                captured.Add(string.Empty);
                if (TryEndpoint(node.CatchAll, method, captured, allowed) is { } catchAllHit) return catchAllHit;
                captured.RemoveAt(captured.Count - 1);
            }

            return null;
        }

        var segment = segments[index];

        if (node.Literals.TryGetValue(segment, out var literal) &&
            Search(literal, segments, index + 1, method, captured, allowed) is { } literalHit)
            return literalHit;

        foreach (var (constraint, child) in node.Parameters)
        {
            if (constraint is not null && !constraint.Match(segment)) continue;
            captured.Add(segment);
            if (Search(child, segments, index + 1, method, captured, allowed) is { } parameterHit) return parameterHit;
            captured.RemoveAt(captured.Count - 1);
        }

        if (node.CatchAll is not null)
        {
            captured.Add(string.Join('/', segments[index..]));
            if (TryEndpoint(node.CatchAll, method, captured, allowed) is { } catchAllHit) return catchAllHit;
            captured.RemoveAt(captured.Count - 1);
        }

        return null;
    }

    private static (Endpoint, List<string>)? TryEndpoint(Node node, string method, List<string> captured, SortedSet<string> allowed)
    {
        if (node.Endpoints.Count == 0) return null;

        if (node.Endpoints.TryGetValue(method, out var endpoint) ||
            (method == "HEAD" && node.Endpoints.TryGetValue("GET", out endpoint))) // HEAD is served by GET handlers
            return (endpoint, new List<string>(captured));

        // The path matched but not the method. Remember the methods this path supports, for a 405 with an Allow header.
        foreach (var m in node.Endpoints.Keys) allowed.Add(m);
        if (node.Endpoints.ContainsKey("GET")) allowed.Add("HEAD");
        return null;
    }

    private void Add(Endpoint endpoint)
    {
        var segments = endpoint.Template.Segments;

        Insert(endpoint, segments);
        // "/users/{id?}" is stored twice: once as "/users/{id}" and once as "/users".
        if (segments.Count > 0 && segments[^1].IsOptional)
            Insert(endpoint, segments.Take(segments.Count - 1).ToList());
    }

    private void Insert(Endpoint endpoint, IReadOnlyList<RouteSegment> segments)
    {
        var node = _root;
        foreach (var segment in segments)
        {
            node = segment.Kind switch
            {
                SegmentKind.Literal => GetOrAdd(node.Literals, segment.Value),
                SegmentKind.Parameter => node.GetOrAddParameter(segment.Constraint),
                _ => node.CatchAll ??= new Node(),
            };
        }

        if (node.Endpoints.TryGetValue(endpoint.HttpMethod, out var existing))
            throw new InvalidOperationException(
                $"Ambiguous routes: '{endpoint.HttpMethod} {endpoint.Template}' and '{existing.HttpMethod} {existing.Template}' match the same requests.");

        node.Endpoints[endpoint.HttpMethod] = endpoint;
    }

    private static Node GetOrAdd(Dictionary<string, Node> nodes, string key)
    {
        if (!nodes.TryGetValue(key, out var node)) nodes[key] = node = new Node();
        return node;
    }

    private sealed class Node
    {
        public Dictionary<string, Node> Literals { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<(IRouteConstraint? Constraint, Node Node)> Parameters { get; } = [];

        public Node? CatchAll { get; set; }

        public Dictionary<string, Endpoint> Endpoints { get; } = new(StringComparer.Ordinal);

        public Node GetOrAddParameter(IRouteConstraint? constraint)
        {
            foreach (var (existing, node) in Parameters)
                if (ReferenceEquals(existing, constraint)) return node;

            var created = new Node();
            Parameters.Add((constraint, created));
            return created;
        }

        /// <summary>Constrained parameters are tried before unconstrained ones, because they are more specific.</summary>
        public void Sort()
        {
            var ordered = Parameters.OrderBy(p => p.Constraint is null ? 1 : 0).ToList();
            Parameters.Clear();
            Parameters.AddRange(ordered);
            foreach (var child in Literals.Values) child.Sort();
            foreach (var (_, child) in Parameters) child.Sort();
            CatchAll?.Sort();
        }
    }
}

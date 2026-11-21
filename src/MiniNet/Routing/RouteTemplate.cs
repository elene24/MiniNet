namespace MiniNet.Routing;

public enum SegmentKind
{
    Literal,
    Parameter,
    CatchAll,
}

public sealed record RouteSegment(SegmentKind Kind, string Value, IRouteConstraint? Constraint = null, bool IsOptional = false)
{
    /// <summary>
    /// Two parameter segments with the same constraint are the same node in the route tree, whatever
    /// their names. That is how "/users/{id}" and "/users/{name}" are detected as ambiguous.
    /// </summary>
    public string TreeKey => Kind switch
    {
        SegmentKind.Literal => Value,
        SegmentKind.Parameter => "{" + (Constraint?.Name ?? "") + "}",
        _ => "{*}",
    };
}

/// <summary>
/// A parsed route template such as <c>/users/{id:int}/orders/{orderId?}</c>.
/// Supported: literal segments, <c>{name}</c>, constraints <c>{name:int}</c>, an optional last
/// parameter <c>{name?}</c>, and a catch-all last parameter <c>{*name}</c>.
/// </summary>
public sealed class RouteTemplate
{
    private RouteTemplate(string text, IReadOnlyList<RouteSegment> segments)
    {
        Text = text;
        Segments = segments;
        ParameterNames = segments.Where(s => s.Kind != SegmentKind.Literal).Select(s => s.Value).ToList();
    }

    public string Text { get; }

    public IReadOnlyList<RouteSegment> Segments { get; }

    /// <summary>Parameter names in the order they appear, matching the order values are captured in.</summary>
    public IReadOnlyList<string> ParameterNames { get; }

    public static RouteTemplate Parse(string template)
    {
        if (!template.StartsWith('/'))
            throw new FormatException($"Route template '{template}' must start with '/'.");

        var parts = template.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var segments = new List<RouteSegment>(parts.Length);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            var isLast = i == parts.Length - 1;

            if (!part.Contains('{') && !part.Contains('}'))
            {
                segments.Add(new RouteSegment(SegmentKind.Literal, part));
                continue;
            }

            if (!part.StartsWith('{') || !part.EndsWith('}') || part.Count(c => c == '{') != 1)
                throw new FormatException($"Route template '{template}': a parameter must take up a whole segment, like '{{id}}'.");

            var inner = part[1..^1];
            var segment = ParseParameter(template, inner);

            if (segment.Kind == SegmentKind.CatchAll && !isLast)
                throw new FormatException($"Route template '{template}': a catch-all parameter must be the last segment.");
            if (segment.IsOptional && !isLast)
                throw new FormatException($"Route template '{template}': only the last parameter can be optional.");
            if (!names.Add(segment.Value))
                throw new FormatException($"Route template '{template}': parameter '{segment.Value}' appears twice.");

            segments.Add(segment);
        }

        return new RouteTemplate(template, segments);
    }

    private static RouteSegment ParseParameter(string template, string inner)
    {
        var isCatchAll = inner.StartsWith('*');
        if (isCatchAll) inner = inner[1..];

        var isOptional = inner.EndsWith('?');
        if (isOptional) inner = inner[..^1];

        string name;
        IRouteConstraint? constraint = null;
        var colon = inner.IndexOf(':');
        if (colon >= 0)
        {
            name = inner[..colon];
            var constraintName = inner[(colon + 1)..];
            constraint = RouteConstraints.Get(constraintName)
                ?? throw new FormatException($"Route template '{template}': unknown constraint '{constraintName}'.");
        }
        else
        {
            name = inner;
        }

        if (name.Length == 0 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            throw new FormatException($"Route template '{template}': invalid parameter name '{name}'.");

        if (isCatchAll)
        {
            if (isOptional || constraint is not null)
                throw new FormatException($"Route template '{template}': a catch-all parameter cannot be optional or constrained.");
            return new RouteSegment(SegmentKind.CatchAll, name);
        }

        return new RouteSegment(SegmentKind.Parameter, name, constraint, isOptional);
    }

    public override string ToString() => Text;
}

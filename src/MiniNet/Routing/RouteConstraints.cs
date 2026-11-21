using System.Globalization;

namespace MiniNet.Routing;

public interface IRouteConstraint
{
    string Name { get; }

    bool Match(string value);
}

public static class RouteConstraints
{
    private static readonly Dictionary<string, IRouteConstraint> Constraints = new(StringComparer.OrdinalIgnoreCase)
    {
        ["int"] = new Constraint("int", v => int.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _)),
        ["long"] = new Constraint("long", v => long.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _)),
        ["guid"] = new Constraint("guid", v => Guid.TryParse(v, out _)),
        ["bool"] = new Constraint("bool", v => bool.TryParse(v, out _)),
        ["alpha"] = new Constraint("alpha", v => v.Length > 0 && v.All(char.IsAsciiLetter)),
    };

    public static IRouteConstraint? Get(string name) => Constraints.GetValueOrDefault(name);

    /// <summary>Lets an application add its own constraints, for example "slug".</summary>
    public static void Register(IRouteConstraint constraint) => Constraints[constraint.Name] = constraint;

    private sealed record Constraint(string Name, Func<string, bool> Predicate) : IRouteConstraint
    {
        public bool Match(string value) => Predicate(value);
    }
}

namespace MiniNet.Endpoints;

/// <summary>Binds a handler parameter from a route value. Usually inferred: a parameter named like a route parameter binds from the route.</summary>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class FromRouteAttribute(string? name = null) : Attribute
{
    public string? Name { get; } = name;
}

/// <summary>Binds from the query string. Inferred for simple types (numbers, strings, dates, enums, Guid) that are not route values.</summary>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class FromQueryAttribute(string? name = null) : Attribute
{
    public string? Name { get; } = name;
}

/// <summary>Binds from a request header. Never inferred.</summary>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class FromHeaderAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

/// <summary>Binds by deserializing the JSON body. Inferred for complex types that are not registered services.</summary>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class FromBodyAttribute : Attribute;

/// <summary>Resolves from the request's DI scope. Inferred for registered services.</summary>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class FromServicesAttribute : Attribute;

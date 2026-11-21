using System.Reflection;

namespace MiniNet.DependencyInjection;

/// <summary>Turns registrations into validated <see cref="CallSite"/> graphs.</summary>
internal sealed class CallSiteFactory
{
    private readonly Dictionary<Type, List<ServiceDescriptor>> _descriptors = new();
    private readonly Dictionary<Type, CallSite> _builtIns = new();
    private readonly Dictionary<(ServiceDescriptor, Type), CallSite> _cache = new();
    private readonly object _lock = new();

    public CallSiteFactory(IEnumerable<ServiceDescriptor> descriptors)
    {
        foreach (var descriptor in descriptors)
        {
            if (!_descriptors.TryGetValue(descriptor.ServiceType, out var list))
                _descriptors[descriptor.ServiceType] = list = [];
            list.Add(descriptor);
        }
    }

    public IEnumerable<ServiceDescriptor> Descriptors => _descriptors.Values.SelectMany(d => d);

    public void AddBuiltIn(Type serviceType, CallSite callSite) => _builtIns[serviceType] = callSite;

    /// <summary>Returns null when the type is not registered. Throws when it is registered but cannot be built.</summary>
    public CallSite? GetCallSite(Type serviceType)
    {
        lock (_lock) return Create(serviceType, new ResolutionChain());
    }

    /// <summary>A cheap check that does not build anything.</summary>
    public bool IsRegistered(Type serviceType) =>
        _builtIns.ContainsKey(serviceType) ||
        _descriptors.ContainsKey(serviceType) ||
        (serviceType.IsConstructedGenericType &&
         (serviceType.GetGenericTypeDefinition() == typeof(IEnumerable<>) || _descriptors.ContainsKey(serviceType.GetGenericTypeDefinition())));

    private CallSite? Create(Type serviceType, ResolutionChain chain)
    {
        if (_builtIns.TryGetValue(serviceType, out var builtIn)) return builtIn;

        if (_descriptors.TryGetValue(serviceType, out var exact))
            return FromDescriptor(exact[^1], serviceType, chain); // last registration wins

        if (serviceType.IsConstructedGenericType)
        {
            var definition = serviceType.GetGenericTypeDefinition();

            if (definition == typeof(IEnumerable<>))
                return CreateEnumerable(serviceType.GenericTypeArguments[0], chain);

            if (_descriptors.TryGetValue(definition, out var open))
                return FromDescriptor(open[^1], serviceType, chain);
        }

        return null;
    }

    /// <summary>IEnumerable&lt;T&gt; resolves to every registration of T, in registration order.</summary>
    private CallSite CreateEnumerable(Type itemType, ResolutionChain chain)
    {
        var matching = new List<ServiceDescriptor>();
        if (_descriptors.TryGetValue(itemType, out var exact)) matching.AddRange(exact);
        if (itemType.IsConstructedGenericType && _descriptors.TryGetValue(itemType.GetGenericTypeDefinition(), out var open))
            matching.AddRange(open);

        var items = matching.Select(d => FromDescriptor(d, itemType, chain)).ToArray();
        return new EnumerableCallSite(itemType, items);
    }

    private CallSite FromDescriptor(ServiceDescriptor descriptor, Type serviceType, ResolutionChain chain)
    {
        var key = (descriptor, serviceType);
        if (_cache.TryGetValue(key, out var cached)) return cached;

        chain.Enter(serviceType);
        try
        {
            CallSite site;
            if (descriptor.Instance is not null)
            {
                site = new ConstantCallSite(serviceType, descriptor.Instance) { CacheKey = key };
            }
            else if (descriptor.Factory is not null)
            {
                site = new FactoryCallSite(serviceType, descriptor.Lifetime, descriptor.Factory) { CacheKey = key };
            }
            else
            {
                var implementationType = descriptor.ImplementationType!;
                if (implementationType.IsGenericTypeDefinition)
                    implementationType = implementationType.MakeGenericType(serviceType.GenericTypeArguments);

                var (constructor, parameters) = SelectConstructor(implementationType, chain);
                site = new ConstructorCallSite(serviceType, descriptor.Lifetime, constructor, parameters) { CacheKey = key };
            }

            _cache[key] = site;
            return site;
        }
        finally
        {
            chain.Exit();
        }
    }

    /// <summary>
    /// Picks the constructor with the most parameters that can all be satisfied, like Microsoft's container.
    /// Two satisfiable constructors with the same number of parameters is an error, because neither is obviously right.
    /// </summary>
    private (ConstructorInfo, CallSite[]) SelectConstructor(Type implementationType, ResolutionChain chain)
    {
        var constructors = implementationType.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .OrderByDescending(c => c.GetParameters().Length)
            .ToList();

        if (constructors.Count == 0)
            throw new InvalidOperationException($"'{TypeNames.Format(implementationType)}' has no public constructor.");

        ConstructorInfo? best = null;
        CallSite[]? bestParameters = null;
        string? firstProblem = null;

        foreach (var constructor in constructors)
        {
            var parameters = constructor.GetParameters();
            if (best is not null && parameters.Length < bestParameters!.Length) break;

            var sites = new CallSite[parameters.Length];
            var satisfiable = true;
            for (var i = 0; i < parameters.Length; i++)
            {
                var parameter = parameters[i];
                var site = Create(parameter.ParameterType, chain);
                if (site is null && parameter.HasDefaultValue)
                    site = new ConstantCallSite(parameter.ParameterType, parameter.DefaultValue);

                if (site is null)
                {
                    satisfiable = false;
                    firstProblem ??= $"Unable to resolve service for type '{TypeNames.Format(parameter.ParameterType)}' " +
                                     $"while attempting to activate '{TypeNames.Format(implementationType)}'. Resolution chain: {chain}.";
                    break;
                }

                sites[i] = site;
            }

            if (!satisfiable) continue;

            if (best is not null)
                throw new InvalidOperationException(
                    $"'{TypeNames.Format(implementationType)}' has more than one constructor with {parameters.Length} resolvable parameters. Remove one or make it non-public.");

            best = constructor;
            bestParameters = sites;
        }

        return best is null
            ? throw new InvalidOperationException(firstProblem)
            : (best, bestParameters!);
    }

    /// <summary>Tracks what is being built right now, to catch A → B → C → A before it overflows the stack.</summary>
    private sealed class ResolutionChain
    {
        private readonly List<Type> _types = [];

        public void Enter(Type type)
        {
            if (_types.Contains(type))
            {
                var cycle = _types.SkipWhile(t => t != type).Append(type).Select(TypeNames.Format);
                throw new InvalidOperationException($"Circular dependency detected: {string.Join(" -> ", cycle)}.");
            }

            _types.Add(type);
        }

        public void Exit() => _types.RemoveAt(_types.Count - 1);

        public override string ToString() => _types.Count == 0 ? "(root)" : string.Join(" -> ", _types.Select(TypeNames.Format));
    }
}

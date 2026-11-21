using System.Reflection;

namespace MiniNet.DependencyInjection;

/// <summary>
/// Creates an object that is not itself registered, filling its constructor from explicit arguments
/// first and the container second. Used to build class-based middleware.
/// </summary>
public static class ActivatorUtilities
{
    public static object CreateInstance(IServiceProvider services, Type type, params object[] arguments)
    {
        var constructors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .OrderByDescending(c => c.GetParameters().Length);

        string? problem = null;
        foreach (var constructor in constructors)
        {
            var parameters = constructor.GetParameters();
            var values = new object?[parameters.Length];
            var unused = arguments.ToList();
            var ok = true;

            for (var i = 0; i < parameters.Length && ok; i++)
            {
                var parameter = parameters[i];
                var given = unused.FirstOrDefault(a => parameter.ParameterType.IsInstanceOfType(a));
                if (given is not null)
                {
                    values[i] = given;
                    unused.Remove(given);
                }
                else if (services.GetService(parameter.ParameterType) is { } service)
                {
                    values[i] = service;
                }
                else if (parameter.HasDefaultValue)
                {
                    values[i] = parameter.DefaultValue;
                }
                else
                {
                    ok = false;
                    problem ??= $"Unable to resolve '{TypeNames.Format(parameter.ParameterType)}' for '{TypeNames.Format(type)}'.";
                }
            }

            if (ok && unused.Count == 0)
                return constructor.Invoke(BindingFlags.DoNotWrapExceptions, binder: null, values, culture: null);
        }

        throw new InvalidOperationException(problem ?? $"No suitable constructor found for '{TypeNames.Format(type)}'.");
    }

    public static T CreateInstance<T>(IServiceProvider services, params object[] arguments) =>
        (T)CreateInstance(services, typeof(T), arguments);
}

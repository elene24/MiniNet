using System.ComponentModel.DataAnnotations;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using MiniNet.DependencyInjection;
using MiniNet.Http;
using MiniNet.Pipeline;
using MiniNet.Routing;

namespace MiniNet.Endpoints;

/// <summary>
/// Turns an ordinary delegate such as <c>(int id, CreateOrder body, IOrderService orders) =&gt; ...</c>
/// into a <see cref="RequestDelegate"/>.
///
/// Everything expensive happens once, when the endpoint is mapped: deciding where each parameter
/// comes from, finding converters, and compiling a strongly typed invoker. Per request, the
/// generated delegate only reads values, converts them, and calls the handler.
///
/// Where a parameter comes from, in priority order:
/// 1. An explicit [FromRoute], [FromQuery], [FromHeader], [FromBody] or [FromServices] attribute.
/// 2. HttpContext, HttpRequest, HttpResponse and CancellationToken are supplied directly.
/// 3. A name matching a route parameter binds from the route.
/// 4. A simple type (number, string, date, Guid, enum) binds from the query string.
/// 5. A type registered in DI is resolved from the request scope.
/// 6. Anything else is the JSON body. At most one parameter may come from the body.
/// </summary>
public static class RequestDelegateFactory
{
    private static readonly NullabilityInfoContext Nullability = new();

    public static RequestDelegate Create(Delegate handler, RouteTemplate template, IServiceProviderIsService serviceChecker)
    {
        var method = handler.Method;
        var binders = method.GetParameters().Select(p => CreateBinder(p, template, serviceChecker)).ToArray();

        if (binders.Count(b => b.Source == BindingSource.Body) > 1)
            throw new InvalidOperationException(
                $"Handler for '{template}' has more than one parameter bound from the request body. Wrap them in one type.");

        var invoke = CompileInvoker(handler);
        var resultReader = ResultReader.For(method.ReturnType);

        return async context =>
        {
            var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            var arguments = new object?[binders.Length];
            for (var i = 0; i < binders.Length; i++)
            {
                var outcome = binders[i].Bind(context);
                if (outcome.StatusCode is { } failureStatus)
                {
                    // 415 means the body was not JSON at all; report that on its own.
                    if (failureStatus == 415)
                    {
                        await Results.Problem(415, "Unsupported media type.", outcome.Error).ExecuteAsync(context);
                        return;
                    }

                    errors[binders[i].DisplayName] = [outcome.Error!];
                    continue;
                }

                arguments[i] = outcome.Value;
                if (binders[i].Source == BindingSource.Body && outcome.Value is not null)
                    Validate(outcome.Value, errors);
            }

            if (errors.Count > 0)
            {
                await Results.ValidationProblem(errors).ExecuteAsync(context);
                return;
            }

            var returned = invoke(arguments);
            var (hasValue, value) = await resultReader.ReadAsync(returned);
            if (hasValue) await WriteResultAsync(context, value);
        };
    }

    private static async Task WriteResultAsync(HttpContext context, object? value)
    {
        switch (value)
        {
            case IResult result:
                await result.ExecuteAsync(context);
                break;
            case string text:
                await context.Response.WriteAsync(text, context.RequestAborted);
                break;
            case null:
                await context.Response.WriteJsonAsync<object?>(null, cancellationToken: context.RequestAborted);
                break;
            default:
                await context.Response.WriteJsonAsync(value, value.GetType(), cancellationToken: context.RequestAborted);
                break;
        }
    }

    /// <summary>Runs [Required], [Range], [StringLength] and similar attributes on a bound body.</summary>
    private static void Validate(object value, Dictionary<string, string[]> errors)
    {
        var results = new List<ValidationResult>();
        if (Validator.TryValidateObject(value, new ValidationContext(value), results, validateAllProperties: true)) return;

        foreach (var result in results)
        {
            var members = result.MemberNames.Any() ? result.MemberNames : [""];
            foreach (var member in members)
            {
                var key = JsonNamingPolicy.CamelCase.ConvertName(member); // match the JSON property name the client sent
                errors[key] = errors.TryGetValue(key, out var existing)
                    ? [.. existing, result.ErrorMessage ?? "Invalid value."]
                    : [result.ErrorMessage ?? "Invalid value."];
            }
        }
    }

    // ------------------------------------------------------------------ binding

    private enum BindingSource
    {
        Special,
        Route,
        Query,
        Header,
        Body,
        Services,
    }

    private readonly record struct BindOutcome(object? Value, int? StatusCode = null, string? Error = null)
    {
        public static BindOutcome Fail(string error, int statusCode = 400) => new(null, statusCode, error);
    }

    private sealed record ParameterBinder(string DisplayName, BindingSource Source, Func<HttpContext, BindOutcome> Bind);

    private static ParameterBinder CreateBinder(ParameterInfo parameter, RouteTemplate template, IServiceProviderIsService serviceChecker)
    {
        var type = parameter.ParameterType;
        var name = parameter.Name!;
        var optional = IsOptional(parameter);
        var fallback = parameter.HasDefaultValue ? parameter.DefaultValue : null;

        // 1. Explicit attributes
        if (parameter.GetCustomAttribute<FromRouteAttribute>() is { } fromRoute)
            return RouteBinder(fromRoute.Name ?? name);
        if (parameter.GetCustomAttribute<FromQueryAttribute>() is { } fromQuery)
            return StringBinder(fromQuery.Name ?? name, BindingSource.Query, c => c.Request.Query.GetValueOrDefault(fromQuery.Name ?? name));
        if (parameter.GetCustomAttribute<FromHeaderAttribute>() is { } fromHeader)
            return StringBinder(fromHeader.Name, BindingSource.Header, c => c.Request.Headers[fromHeader.Name]);
        if (parameter.GetCustomAttribute<FromBodyAttribute>() is not null)
            return BodyBinder();
        if (parameter.GetCustomAttribute<FromServicesAttribute>() is not null)
            return ServiceBinder();

        // 2. Special types
        if (type == typeof(HttpContext)) return Special(c => c);
        if (type == typeof(HttpRequest)) return Special(c => c.Request);
        if (type == typeof(HttpResponse)) return Special(c => c.Response);
        if (type == typeof(CancellationToken)) return Special(c => c.RequestAborted);

        // 3-6. Inference
        if (template.ParameterNames.Contains(name, StringComparer.OrdinalIgnoreCase)) return RouteBinder(name);
        if (SimpleTypes.IsSimple(type))
            return StringBinder(name, BindingSource.Query, c => c.Request.Query.GetValueOrDefault(name));
        if (serviceChecker.IsService(type)) return ServiceBinder();
        if (type.IsInterface || type.IsAbstract)
            throw new InvalidOperationException(
                $"Handler for '{template}': parameter '{name}' has type '{type.Name}', which is not a registered service and cannot be read from the body.");
        return BodyBinder();

        ParameterBinder Special(Func<HttpContext, object> read) =>
            new(name, BindingSource.Special, c => new BindOutcome(read(c)));

        ParameterBinder RouteBinder(string routeName)
        {
            if (!template.ParameterNames.Contains(routeName, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Handler for '{template}': [FromRoute] parameter '{routeName}' is not in the route template.");
            return StringBinder(routeName, BindingSource.Route, c => c.Request.RouteValues.GetValueOrDefault(routeName));
        }

        ParameterBinder StringBinder(string sourceName, BindingSource source, Func<HttpContext, string?> read)
        {
            if (!SimpleTypes.IsSimple(type))
                throw new InvalidOperationException($"Handler for '{template}': '{name}' has type '{type.Name}', which cannot be read from a string.");

            var where = source.ToString().ToLowerInvariant();
            return new ParameterBinder(sourceName, source, context =>
            {
                var raw = read(context);
                if (string.IsNullOrEmpty(raw))
                {
                    return optional
                        ? new BindOutcome(fallback)
                        : BindOutcome.Fail($"The {where} value '{sourceName}' is required.");
                }

                return SimpleTypes.TryParse(raw, type, out var value)
                    ? new BindOutcome(value)
                    : BindOutcome.Fail($"The {where} value '{sourceName}' must be a valid {FriendlyName(type)}.");
            });
        }

        ParameterBinder BodyBinder() => new(name, BindingSource.Body, context =>
        {
            var request = context.Request;
            if (request.Body.Length == 0)
                return optional ? new BindOutcome(fallback) : BindOutcome.Fail("A JSON request body is required.");

            if (request.ContentType is { } contentType && !contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
                return BindOutcome.Fail($"Expected a JSON body but got '{contentType}'.", 415);

            try
            {
                var value = JsonSerializer.Deserialize(request.Body, type, MiniJson.Options);
                return value is null && !optional ? BindOutcome.Fail("A JSON request body is required.") : new BindOutcome(value);
            }
            catch (JsonException ex)
            {
                return BindOutcome.Fail($"The request body is not valid JSON: {ex.Message}");
            }
        });

        ParameterBinder ServiceBinder() => new(name, BindingSource.Services, context =>
            new BindOutcome(optional ? context.RequestServices.GetService(type) : context.RequestServices.GetRequiredService(type)));
    }

    /// <summary>A parameter is optional when it has a default value or is nullable (int?, string?).</summary>
    private static bool IsOptional(ParameterInfo parameter)
    {
        if (parameter.HasDefaultValue) return true;
        if (parameter.ParameterType.IsValueType) return Nullable.GetUnderlyingType(parameter.ParameterType) is not null;
        return Nullability.Create(parameter).WriteState == NullabilityState.Nullable;
    }

    private static string FriendlyName(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type switch
        {
            _ when type == typeof(int) || type == typeof(long) => "integer",
            _ when type == typeof(Guid) => "GUID",
            _ when type == typeof(DateOnly) => "date (yyyy-MM-dd)",
            _ when type.IsEnum => $"value ({string.Join(", ", Enum.GetNames(type))})",
            _ => type.Name,
        };
    }

    // ------------------------------------------------------------------ invocation

    /// <summary>Compiles <c>args =&gt; (object)handler((int)args[0], (Order)args[1], ...)</c>, so no reflection runs per request.</summary>
    private static Func<object?[], object?> CompileInvoker(Delegate handler)
    {
        var method = handler.Method;
        var args = Expression.Parameter(typeof(object?[]), "args");
        var parameters = method.GetParameters()
            .Select((p, i) => (Expression)Expression.Convert(Expression.ArrayIndex(args, Expression.Constant(i)), p.ParameterType));

        var target = handler.Target is null ? null : Expression.Constant(handler.Target);
        Expression call = Expression.Call(target, method, parameters);

        call = method.ReturnType == typeof(void)
            ? Expression.Block(call, Expression.Constant(null, typeof(object)))
            : Expression.Convert(call, typeof(object));

        return Expression.Lambda<Func<object?[], object?>>(call, args).Compile();
    }

    /// <summary>
    /// Knows, from the declared return type, how to unwrap Task, Task&lt;T&gt;, ValueTask and ValueTask&lt;T&gt;.
    /// The unwrapping code is compiled once per endpoint, so no reflection runs per request.
    /// </summary>
    private sealed class ResultReader
    {
        private readonly bool _hasValue;
        private readonly Func<object, Task>? _toTask;
        private readonly Func<Task, object?>? _getResult;

        private ResultReader(bool hasValue, Func<object, Task>? toTask, Func<Task, object?>? getResult)
        {
            _hasValue = hasValue;
            _toTask = toTask;
            _getResult = getResult;
        }

        public static ResultReader For(Type returnType)
        {
            if (returnType == typeof(void)) return new ResultReader(false, null, null);
            if (returnType == typeof(Task)) return new ResultReader(false, o => (Task)o, null);
            if (returnType == typeof(ValueTask)) return new ResultReader(false, o => ((ValueTask)o).AsTask(), null);

            if (returnType.IsGenericType)
            {
                var definition = returnType.GetGenericTypeDefinition();
                if (definition == typeof(Task<>))
                    return new ResultReader(true, o => (Task)o, CompileResultGetter(returnType));

                if (definition == typeof(ValueTask<>))
                {
                    // o => ((ValueTask<T>)o).AsTask()
                    var asTask = returnType.GetMethod(nameof(ValueTask<int>.AsTask))!;
                    var boxed = Expression.Parameter(typeof(object), "o");
                    var toTask = Expression.Lambda<Func<object, Task>>(
                        Expression.Call(Expression.Convert(boxed, returnType), asTask), boxed).Compile();
                    return new ResultReader(true, toTask, CompileResultGetter(asTask.ReturnType));
                }
            }

            return new ResultReader(true, null, null);
        }

        /// <summary>Compiles <c>t =&gt; (object)((Task&lt;T&gt;)t).Result</c>.</summary>
        private static Func<Task, object?> CompileResultGetter(Type taskType)
        {
            var task = Expression.Parameter(typeof(Task), "t");
            var result = Expression.Property(Expression.Convert(task, taskType), nameof(Task<int>.Result));
            return Expression.Lambda<Func<Task, object?>>(Expression.Convert(result, typeof(object)), task).Compile();
        }

        public async Task<(bool HasValue, object? Value)> ReadAsync(object? returned)
        {
            if (_toTask is null) return (_hasValue, returned);

            var task = _toTask(returned!);
            await task;
            return _hasValue ? (true, _getResult!(task)) : (false, null);
        }
    }
}

using MiniNet.Http;

namespace MiniNet.Endpoints;

/// <summary>A handler can return an IResult to control the status code, headers and body explicitly.</summary>
public interface IResult
{
    Task ExecuteAsync(HttpContext context);
}

/// <summary>RFC 9457 "problem details": the standard JSON shape for HTTP API errors.</summary>
public sealed class ProblemDetails
{
    public string? Type { get; set; }

    public string? Title { get; set; }

    public int? Status { get; set; }

    public string? Detail { get; set; }

    public string? TraceId { get; set; }

    public IDictionary<string, string[]>? Errors { get; set; }
}

public static class Results
{
    public static IResult Ok(object? value = null) => new JsonResult(200, value);

    public static IResult Created(string location, object? value = null) => new JsonResult(201, value, ("Location", location));

    public static IResult NoContent() => new StatusResult(204);

    public static IResult NotFound(object? value = null) => new JsonResult(404, value);

    public static IResult BadRequest(object? value = null) => new JsonResult(400, value);

    public static IResult Conflict(object? value = null) => new JsonResult(409, value);

    public static IResult StatusCode(int statusCode) => new StatusResult(statusCode);

    public static IResult Json(object? value, int statusCode = 200) => new JsonResult(statusCode, value);

    public static IResult Text(string text, string contentType = "text/plain; charset=utf-8", int statusCode = 200) =>
        new TextResult(statusCode, text, contentType);

    public static IResult Problem(int statusCode, string title, string? detail = null) =>
        new ProblemResult(new ProblemDetails { Status = statusCode, Title = title, Detail = detail });

    public static IResult ValidationProblem(IDictionary<string, string[]> errors) =>
        new ProblemResult(new ProblemDetails
        {
            Status = 400,
            Title = "One or more validation errors occurred.",
            Errors = errors,
        });

    private sealed class StatusResult(int statusCode) : IResult
    {
        public Task ExecuteAsync(HttpContext context)
        {
            context.Response.StatusCode = statusCode;
            return Task.CompletedTask;
        }
    }

    private sealed class JsonResult(int statusCode, object? value, params (string Name, string Value)[] headers) : IResult
    {
        public Task ExecuteAsync(HttpContext context)
        {
            context.Response.StatusCode = statusCode;
            foreach (var (name, headerValue) in headers) context.Response.Headers[name] = headerValue;
            return value is null
                ? Task.CompletedTask
                : context.Response.WriteJsonAsync(value, value.GetType(), cancellationToken: context.RequestAborted);
        }
    }

    private sealed class TextResult(int statusCode, string text, string contentType) : IResult
    {
        public Task ExecuteAsync(HttpContext context)
        {
            context.Response.StatusCode = statusCode;
            context.Response.ContentType = contentType;
            return context.Response.WriteAsync(text, context.RequestAborted);
        }
    }

    private sealed class ProblemResult(ProblemDetails problem) : IResult
    {
        public async Task ExecuteAsync(HttpContext context)
        {
            problem.TraceId ??= context.TraceIdentifier;
            context.Response.StatusCode = problem.Status ?? 500;
            await context.Response.WriteJsonAsync(problem, cancellationToken: context.RequestAborted);
            context.Response.ContentType = "application/problem+json; charset=utf-8";
        }
    }
}

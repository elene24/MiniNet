using MiniNet.Http;
using MiniNet.Pipeline;

namespace MiniNet.Middleware;

public sealed class StaticFileOptions
{
    public required string RootDirectory { get; init; }

    /// <summary>Default document served for a directory request.</summary>
    public string DefaultDocument { get; init; } = "index.html";
}

/// <summary>
/// Serves files from a directory for GET and HEAD requests, and passes everything else on.
/// The important part is the path traversal check: "/../../etc/passwd" must never escape the root.
/// </summary>
public sealed class StaticFilesMiddleware(RequestDelegate next, StaticFileOptions options)
{
    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".txt"] = "text/plain; charset=utf-8",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".ico"] = "image/x-icon",
    };

    private readonly string _root = Path.GetFullPath(options.RootDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

    public async Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        if (request.Method is not ("GET" or "HEAD") || ResolveFile(request.Path) is not { } file)
        {
            await next(context);
            return;
        }

        context.Response.ContentType = ContentTypes.GetValueOrDefault(Path.GetExtension(file), "application/octet-stream");
        await using var stream = File.OpenRead(file);
        await stream.CopyToAsync(context.Response.Body, context.RequestAborted);
    }

    internal string? ResolveFile(string requestPath)
    {
        var relative = requestPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        if (relative.Contains('\0')) return null;

        // GetFullPath collapses "..": the result must still sit inside the root directory.
        var full = Path.GetFullPath(Path.Combine(_root, relative));
        if (!full.StartsWith(_root, StringComparison.Ordinal) && full + Path.DirectorySeparatorChar != _root)
            return null;

        if (Directory.Exists(full)) full = Path.Combine(full, options.DefaultDocument);
        return File.Exists(full) && full.StartsWith(_root, StringComparison.Ordinal) ? full : null;
    }
}

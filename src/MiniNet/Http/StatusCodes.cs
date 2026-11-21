namespace MiniNet.Http;

public static class StatusCodes
{
    public static string ReasonPhrase(int statusCode) => statusCode switch
    {
        100 => "Continue",
        200 => "OK",
        201 => "Created",
        202 => "Accepted",
        204 => "No Content",
        301 => "Moved Permanently",
        302 => "Found",
        304 => "Not Modified",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        408 => "Request Timeout",
        409 => "Conflict",
        413 => "Content Too Large",
        414 => "URI Too Long",
        415 => "Unsupported Media Type",
        422 => "Unprocessable Content",
        429 => "Too Many Requests",
        431 => "Request Header Fields Too Large",
        500 => "Internal Server Error",
        501 => "Not Implemented",
        503 => "Service Unavailable",
        505 => "HTTP Version Not Supported",
        _ => "Unknown",
    };

    /// <summary>Responses with these status codes must not carry a body (RFC 9110 §6.4.1).</summary>
    public static bool AllowsBody(int statusCode) =>
        statusCode is not (204 or 304) && statusCode >= 200;
}

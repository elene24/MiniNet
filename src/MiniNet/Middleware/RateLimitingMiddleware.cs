using System.Collections.Concurrent;
using System.Globalization;
using MiniNet.Endpoints;
using MiniNet.Http;
using MiniNet.Pipeline;

namespace MiniNet.Middleware;

public sealed class RateLimitingOptions
{
    /// <summary>Bucket size: how many requests a client can make in a burst.</summary>
    public int Capacity { get; set; } = 100;

    /// <summary>How fast the bucket refills, in tokens per second.</summary>
    public double TokensPerSecond { get; set; } = 10;

    /// <summary>Which bucket a request belongs to. Defaults to the client IP address.</summary>
    public Func<HttpContext, string> PartitionKey { get; set; } = context => context.RemoteIpAddress?.ToString() ?? "unknown";

    /// <summary>Injected so tests can move time forward instead of sleeping.</summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    /// <summary>Buckets unused for this long are removed so memory does not grow with every client ever seen.</summary>
    public TimeSpan IdleBucketLifetime { get; set; } = TimeSpan.FromMinutes(10);
}

/// <summary>Endpoint metadata that exempts an endpoint from rate limiting, for example a health check.</summary>
public sealed class DisableRateLimitingMetadata
{
    public static readonly DisableRateLimitingMetadata Instance = new();
}

/// <summary>
/// Token-bucket rate limiting per client. Each client has a bucket of <see cref="RateLimitingOptions.Capacity"/>
/// tokens that refills at a steady rate; each request takes one token, and a request that finds the bucket
/// empty gets 429 with a Retry-After header. Unlike a fixed window ("100 per minute, reset on the minute"),
/// a token bucket allows short bursts without the double burst at window boundaries.
///
/// Place it after UseRouting so that endpoints marked with <see cref="DisableRateLimitingMetadata"/> can be exempted.
/// </summary>
public sealed class RateLimitingMiddleware(RequestDelegate next, RateLimitingOptions options)
{
    private readonly ConcurrentDictionary<string, TokenBucket> _buckets = new();
    private long _requestCount;

    public int BucketCount => _buckets.Count;

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Endpoint?.GetMetadata<DisableRateLimitingMetadata>() is not null)
        {
            await next(context);
            return;
        }

        var now = options.TimeProvider.GetUtcNow();
        var key = options.PartitionKey(context);
        var bucket = _buckets.GetOrAdd(key, _ => new TokenBucket(options.Capacity, now));
        var (allowed, remaining, retryAfter) = bucket.TryTake(now, options.Capacity, options.TokensPerSecond);

        context.Response.Headers["X-RateLimit-Limit"] = options.Capacity.ToString(CultureInfo.InvariantCulture);
        context.Response.Headers["X-RateLimit-Remaining"] = remaining.ToString(CultureInfo.InvariantCulture);

        if (Interlocked.Increment(ref _requestCount) % 1000 == 0) RemoveIdleBuckets(now);

        if (!allowed)
        {
            context.Response.Headers["Retry-After"] = Math.Ceiling(retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
            await Results.Problem(429, "Too many requests.", "Rate limit exceeded. Try again later.").ExecuteAsync(context);
            return;
        }

        await next(context);
    }

    private void RemoveIdleBuckets(DateTimeOffset now)
    {
        foreach (var (key, bucket) in _buckets)
            if (now - bucket.LastSeen > options.IdleBucketLifetime)
                _buckets.TryRemove(key, out _);
    }

    private sealed class TokenBucket(int capacity, DateTimeOffset createdAt)
    {
        private readonly object _lock = new();
        private double _tokens = capacity;
        private DateTimeOffset _lastRefill = createdAt;

        public DateTimeOffset LastSeen { get; private set; } = createdAt;

        public (bool Allowed, int Remaining, TimeSpan RetryAfter) TryTake(DateTimeOffset now, int capacity, double tokensPerSecond)
        {
            // Check-and-take happens under one lock. Without it, two concurrent requests could both see
            // one token left and both be allowed through.
            lock (_lock)
            {
                var elapsed = (now - _lastRefill).TotalSeconds;
                if (elapsed > 0)
                {
                    _tokens = Math.Min(capacity, _tokens + elapsed * tokensPerSecond);
                    _lastRefill = now;
                }

                LastSeen = now;

                if (_tokens >= 1)
                {
                    _tokens -= 1;
                    return (true, (int)Math.Floor(_tokens), TimeSpan.Zero);
                }

                var secondsUntilToken = (1 - _tokens) / tokensPerSecond;
                return (false, 0, TimeSpan.FromSeconds(secondsUntilToken));
            }
        }
    }
}

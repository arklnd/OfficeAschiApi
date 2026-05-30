using OfficeAschiApi.Caching;

namespace OfficeAschiApi.Middleware;

/// <summary>
/// Returns 503 Service Unavailable for API requests while the write-through
/// cache is still warming up from the database. Non-API requests (e.g. SPA
/// static files) pass through normally.
/// </summary>
public sealed class CacheWarmupMiddleware
{
    private readonly RequestDelegate _next;
    private readonly WriteThroughCache _cache;

    public CacheWarmupMiddleware(RequestDelegate next, WriteThroughCache cache)
    {
        _next = next;
        _cache = cache;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!_cache.IsWarmedUp && context.Request.Path.StartsWithSegments("/api"))
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers.RetryAfter = "5";
            await context.Response.WriteAsJsonAsync(new { error = "Service is starting up, please retry shortly" });
            return;
        }

        await _next(context);
    }
}

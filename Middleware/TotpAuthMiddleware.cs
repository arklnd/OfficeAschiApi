using Microsoft.EntityFrameworkCore;
using OfficeAschiApi.Data;
using OfficeAschiApi.Services;

namespace OfficeAschiApi.Middleware;

/// <summary>
/// TOTP Authentication Middleware.
/// Checks Authorization header for TOTP credentials on endpoints marked with [TotpAuth].
/// 
/// Header format: Authorization: TOTP manager:{teamId}:{code}
///            or: Authorization: TOTP reportee:{reporteeId}:{code}
/// 
/// On success, sets HttpContext.Items["TotpEntityType"] and ["TotpEntityId"].
/// </summary>
public class TotpAuthMiddleware
{
    private readonly RequestDelegate _next;

    public TotpAuthMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, AppDbContext db, TotpService totpService)
    {
        var endpoint = context.GetEndpoint();
        var requiresTotp = endpoint?.Metadata.GetMetadata<TotpAuthAttribute>();

        if (requiresTotp == null)
        {
            await _next(context);
            return;
        }

        var authHeader = context.Request.Headers["Authorization"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(authHeader) || !authHeader.StartsWith("TOTP ", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { error = "Authorization header required. Format: TOTP <type>:<id>:<code>" });
            return;
        }

        var parts = authHeader[5..].Split(':');
        if (parts.Length != 3)
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { error = "Invalid auth format. Expected: TOTP <type>:<id>:<code>" });
            return;
        }

        var entityType = parts[0].ToLowerInvariant();
        if (!int.TryParse(parts[1], out var entityId))
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { error = "Invalid entity ID in auth header" });
            return;
        }
        var totpCode = parts[2];

        // Look up the TOTP secret from DB
        string? secret = null;
        if (entityType == "manager")
        {
            secret = await db.Teams.Where(t => t.Id == entityId).Select(t => t.ManagerTotpSecret).FirstOrDefaultAsync();
        }
        else if (entityType == "reportee")
        {
            secret = await db.Reportees.Where(r => r.Id == entityId).Select(r => r.TotpSecret).FirstOrDefaultAsync();
        }
        else
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { error = "Invalid entity type. Must be 'manager' or 'reportee'" });
            return;
        }

        if (string.IsNullOrEmpty(secret))
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { error = "TOTP not set up for this entity" });
            return;
        }

        if (!totpService.ValidateTotp(secret, totpCode))
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { error = "Invalid TOTP code" });
            return;
        }

        // Store authenticated entity info
        context.Items["TotpEntityType"] = entityType;
        context.Items["TotpEntityId"] = entityId;

        await _next(context);
    }
}

/// <summary>
/// Marker attribute for endpoints requiring TOTP authentication.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public class TotpAuthAttribute : Attribute { }

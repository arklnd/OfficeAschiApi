using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OfficeAschiApi.Data;
using OfficeAschiApi.DTOs;
using OfficeAschiApi.Middleware;
using OfficeAschiApi.Services;

namespace OfficeAschiApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PushController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly VapidSettings _vapid;

    public PushController(AppDbContext db, IOptions<VapidSettings> vapidOptions)
    {
        _db = db;
        _vapid = vapidOptions.Value;
    }

    /// <summary>
    /// Get the VAPID public key for Web Push subscription (no auth required)
    /// </summary>
    [HttpGet("vapid-key")]
    public ActionResult<object> GetVapidKey()
    {
        return Ok(new { publicKey = _vapid.PublicKey });
    }

    /// <summary>
    /// Subscribe to push notifications (TOTP auth required)
    /// </summary>
    [HttpPost("subscribe")]
    [TotpAuth]
    public async Task<ActionResult> Subscribe([FromBody] PushSubscriptionRequest request)
    {
        var entityType = (string)HttpContext.Items["TotpEntityType"]!;
        var entityId = (int)HttpContext.Items["TotpEntityId"]!;

        if (string.IsNullOrWhiteSpace(request.Endpoint))
            return BadRequest(new { error = "Endpoint is required" });

        // Determine teamId based on entity type
        int teamId;
        if (entityType == "manager")
        {
            teamId = entityId;
        }
        else
        {
            var reportee = await _db.Reportees.FindAsync(entityId);
            if (reportee == null) return NotFound(new { error = "Reportee not found" });
            teamId = reportee.TeamId;
        }

        // Upsert: remove existing subscription for same endpoint+entity, then add new
        var existing = await _db.PushSubscriptions.FirstOrDefaultAsync(s =>
            s.Endpoint == request.Endpoint && s.EntityType == entityType && s.EntityId == entityId);

        if (existing != null)
        {
            existing.P256dhKey = request.P256dhKey;
            existing.AuthKey = request.AuthKey;
        }
        else
        {
            _db.PushSubscriptions.Add(new Models.PushSubscription
            {
                TeamId = teamId,
                EntityType = entityType,
                EntityId = entityId,
                Endpoint = request.Endpoint,
                P256dhKey = request.P256dhKey,
                AuthKey = request.AuthKey,
            });
        }

        await _db.SaveChangesAsync();
        return Ok(new { message = "Subscribed to push notifications" });
    }

    /// <summary>
    /// Unsubscribe from push notifications (TOTP auth required)
    /// </summary>
    [HttpPost("unsubscribe")]
    [TotpAuth]
    public async Task<ActionResult> Unsubscribe([FromBody] PushSubscriptionRequest request)
    {
        var entityType = (string)HttpContext.Items["TotpEntityType"]!;
        var entityId = (int)HttpContext.Items["TotpEntityId"]!;

        var sub = await _db.PushSubscriptions.FirstOrDefaultAsync(s =>
            s.Endpoint == request.Endpoint && s.EntityType == entityType && s.EntityId == entityId);

        if (sub != null)
        {
            _db.PushSubscriptions.Remove(sub);
            await _db.SaveChangesAsync();
        }

        return Ok(new { message = "Unsubscribed from push notifications" });
    }
}

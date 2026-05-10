using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OfficeAschiApi.Data;
using OfficeAschiApi.DTOs;
using OfficeAschiApi.Middleware;
using OfficeAschiApi.Models;
using OfficeAschiApi.Services;

namespace OfficeAschiApi.Controllers;

[ApiController]
[Route("api/teams/{teamId}/[controller]")]
public class ReporteesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly TotpService _totpService;
    private readonly WaitlistService _waitlistService;
    private readonly NotificationService _notificationService;

    public ReporteesController(AppDbContext db, TotpService totpService, WaitlistService waitlistService, NotificationService notificationService)
    {
        _db = db;
        _totpService = totpService;
        _waitlistService = waitlistService;
        _notificationService = notificationService;
    }

    /// <summary>
    /// List reportees in a team (public)
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<ReporteeResponse>>> List(int teamId)
    {
        if (!await _db.Teams.AnyAsync(t => t.Id == teamId))
            return NotFound(new { error = "Team not found" });

        var reportees = await _db.Reportees
            .Where(r => r.TeamId == teamId)
            .Select(r => new ReporteeResponse(r.Id, r.FriendlyName, r.TeamId, r.IsApproved, r.TotpSecret != null))
            .ToListAsync();

        return Ok(reportees);
    }

    /// <summary>
    /// Join a team as a reportee with TOTP setup (atomic).
    /// Client provides friendly name + secret key + TOTP code. If code validates, both reportee and TOTP are created together.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<ReporteeResponse>> Join(int teamId, [FromBody] JoinTeamRequest request)
    {
        // Validate TOTP first — fail fast
        if (string.IsNullOrWhiteSpace(request.SecretKey) || string.IsNullOrWhiteSpace(request.TotpCode))
            return BadRequest(new { error = "SecretKey and TotpCode are required to join a team" });

        if (!_totpService.ValidateTotp(request.SecretKey, request.TotpCode))
            return BadRequest(new { error = "TOTP code does not match the secret key. Try again." });

        if (!await _db.Teams.AnyAsync(t => t.Id == teamId))
            return NotFound(new { error = "Team not found" });

        if (string.IsNullOrWhiteSpace(request.FriendlyName))
            return BadRequest(new { error = "Friendly name is required" });

        if (await _db.Reportees.AnyAsync(r => r.TeamId == teamId && r.FriendlyName == request.FriendlyName))
            return Conflict(new { error = "Name already taken in this team" });

        var reportee = new Reportee
        {
            TeamId = teamId,
            FriendlyName = request.FriendlyName,
            IsApproved = false,
            TotpSecret = request.SecretKey
        };

        _db.Reportees.Add(reportee);
        await _db.SaveChangesAsync();

        // Notify manager about new join request
        var team = await _db.Teams.FindAsync(teamId);
        await _notificationService.SendToManagerAsync(teamId, new NotificationPayload(
            "New Join Request",
            $"{reportee.FriendlyName} has requested to join {team?.Name ?? "your team"}",
            $"/team/{teamId}",
            "join_request",
            Guid.NewGuid().ToString()
        ));

        return CreatedAtAction(nameof(List), new { teamId },
            new ReporteeResponse(reportee.Id, reportee.FriendlyName, reportee.TeamId, false, true));
    }

    /// <summary>
    /// Approve a reportee (manager TOTP required)
    /// Authorization: TOTP manager:{teamId}:{code}
    /// </summary>
    [HttpPut("{reporteeId}/approve")]
    [TotpAuth]
    public async Task<ActionResult<ReporteeResponse>> Approve(int teamId, int reporteeId)
    {
        var authId = (int)HttpContext.Items["TotpEntityId"]!;
        var authType = (string)HttpContext.Items["TotpEntityType"]!;
        if (authType != "manager" || authId != teamId)
            return Forbid();

        var reportee = await _db.Reportees.FirstOrDefaultAsync(r => r.Id == reporteeId && r.TeamId == teamId);
        if (reportee == null) return NotFound(new { error = "Reportee not found in this team" });

        if (reportee.IsApproved)
            return BadRequest(new { error = "Reportee already approved" });

        reportee.IsApproved = true;
        await _db.SaveChangesAsync();

        // Notify reportee about approval
        var approveTeam = await _db.Teams.FindAsync(teamId);
        await _notificationService.SendToReporteeAsync(reporteeId, new NotificationPayload(
            "Membership Approved!",
            $"You've been approved to join {approveTeam?.Name ?? "the team"}!",
            $"/team/{teamId}",
            "membership_approved",
            Guid.NewGuid().ToString()
        ));

        return Ok(new ReporteeResponse(reportee.Id, reportee.FriendlyName, reportee.TeamId, true, reportee.TotpSecret != null));
    }

    /// <summary>
    /// Deny a pending reportee's join request (manager TOTP required).
    /// Only works on reportees that are NOT yet approved.
    /// Authorization: TOTP manager:{teamId}:{code}
    /// </summary>
    [HttpDelete("{reporteeId}/deny")]
    [TotpAuth]
    public async Task<ActionResult> Deny(int teamId, int reporteeId)
    {
        var authId = (int)HttpContext.Items["TotpEntityId"]!;
        var authType = (string)HttpContext.Items["TotpEntityType"]!;
        if (authType != "manager" || authId != teamId)
            return Forbid();

        var reportee = await _db.Reportees.FirstOrDefaultAsync(r => r.Id == reporteeId && r.TeamId == teamId);
        if (reportee == null) return NotFound(new { error = "Reportee not found in this team" });

        if (reportee.IsApproved)
            return BadRequest(new { error = "Cannot deny an already approved member. Use remove instead." });

        // Notify reportee about denial before deleting
        var denyTeam = await _db.Teams.FindAsync(teamId);
        await _notificationService.SendToReporteeAsync(reporteeId, new NotificationPayload(
            "Request Denied",
            $"Your request to join {denyTeam?.Name ?? "the team"} was denied",
            "/",
            "membership_denied",
            Guid.NewGuid().ToString()
        ));

        // Clean up subscriptions for this reportee
        await _notificationService.CleanupSubscriptionsAsync("reportee", reporteeId);

        // Clean up any bookings (shouldn't exist for unapproved, but defensive)
        var bookings = await _db.Bookings.Where(b => b.ReporteeId == reporteeId).ToListAsync();
        _db.Bookings.RemoveRange(bookings);

        _db.Reportees.Remove(reportee);
        await _db.SaveChangesAsync();

        return Ok(new { message = "Join request denied", reportee = reportee.FriendlyName });
    }

    /// <summary>
    /// Remove an approved member from the team (manager TOTP required).
    /// Deletes all their bookings, vacates seats, and auto-promotes waitlisted entries.
    /// Authorization: TOTP manager:{teamId}:{code}
    /// </summary>
    [HttpDelete("{reporteeId}")]
    [TotpAuth]
    public async Task<ActionResult> Remove(int teamId, int reporteeId)
    {
        var authId = (int)HttpContext.Items["TotpEntityId"]!;
        var authType = (string)HttpContext.Items["TotpEntityType"]!;
        if (authType != "manager" || authId != teamId)
            return Forbid();

        var reportee = await _db.Reportees.FirstOrDefaultAsync(r => r.Id == reporteeId && r.TeamId == teamId);
        if (reportee == null) return NotFound(new { error = "Reportee not found in this team" });

        // Notify reportee about removal before deleting
        var removeTeam = await _db.Teams.FindAsync(teamId);
        await _notificationService.SendToReporteeAsync(reporteeId, new NotificationPayload(
            "Removed from Team",
            $"You've been removed from {removeTeam?.Name ?? "the team"}. Your bookings have been cancelled.",
            "/",
            "booking_cancelled",
            Guid.NewGuid().ToString()
        ));

        // Collect confirmed bookings for waitlist promotion
        var bookings = await _db.Bookings.Where(b => b.ReporteeId == reporteeId).ToListAsync();
        var confirmedBookings = bookings.Where(b => b.Status == BookingStatus.Confirmed).ToList();

        // Remove all bookings
        _db.Bookings.RemoveRange(bookings);
        await _db.SaveChangesAsync();

        // Promote waitlisted entries for each vacated seat
        foreach (var cb in confirmedBookings)
        {
            await _waitlistService.PromoteWaitlistAsync(teamId, cb.SeatId, cb.Date);
        }

        // Clean up push subscriptions
        await _notificationService.CleanupSubscriptionsAsync("reportee", reporteeId);

        // Remove the reportee
        _db.Reportees.Remove(reportee);
        await _db.SaveChangesAsync();

        return Ok(new
        {
            message = "Member removed",
            reportee = reportee.FriendlyName,
            bookingsRemoved = bookings.Count,
            seatsVacated = confirmedBookings.Count,
            waitlistPromotions = confirmedBookings.Count
        });
    }
}

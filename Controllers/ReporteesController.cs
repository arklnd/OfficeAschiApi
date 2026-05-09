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

    public ReporteesController(AppDbContext db, TotpService totpService)
    {
        _db = db;
        _totpService = totpService;
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
    /// Join a team as a reportee (public - no auth needed to request joining)
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<ReporteeResponse>> Join(int teamId, [FromBody] JoinTeamRequest request)
    {
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
            IsApproved = false
        };

        _db.Reportees.Add(reportee);
        await _db.SaveChangesAsync();

        return CreatedAtAction(nameof(List), new { teamId },
            new ReporteeResponse(reportee.Id, reportee.FriendlyName, reportee.TeamId, false, false));
    }

    /// <summary>
    /// Setup TOTP for a reportee.
    /// Client provides secret key + TOTP code. If code matches, store the secret.
    /// </summary>
    [HttpPost("{reporteeId}/setup-totp")]
    public async Task<ActionResult<TotpSetupResponse>> SetupTotp(int teamId, int reporteeId, [FromBody] TotpSetupRequest request)
    {
        var reportee = await _db.Reportees.FirstOrDefaultAsync(r => r.Id == reporteeId && r.TeamId == teamId);
        if (reportee == null) return NotFound(new { error = "Reportee not found in this team" });

        if (reportee.TotpSecret != null)
            return BadRequest(new { error = "TOTP already set up for this reportee" });

        if (!_totpService.ValidateTotp(request.SecretKey, request.TotpCode))
            return BadRequest(new TotpSetupResponse(false, "TOTP code does not match the secret key"));

        reportee.TotpSecret = request.SecretKey;
        await _db.SaveChangesAsync();

        return Ok(new TotpSetupResponse(true, "TOTP setup successful for reportee"));
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

        return Ok(new ReporteeResponse(reportee.Id, reportee.FriendlyName, reportee.TeamId, true, reportee.TotpSecret != null));
    }
}

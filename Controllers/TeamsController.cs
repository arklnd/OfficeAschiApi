using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OfficeAschiApi.Data;
using OfficeAschiApi.DTOs;
using OfficeAschiApi.Middleware;
using OfficeAschiApi.Models;
using OfficeAschiApi.Services;

namespace OfficeAschiApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class TeamsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly TotpService _totpService;

    public TeamsController(AppDbContext db, TotpService totpService)
    {
        _db = db;
        _totpService = totpService;
    }

    /// <summary>
    /// Search/list teams (public)
    /// </summary>
    /// <param name="q">Optional search query to filter teams by name.</param>
    /// <response code="200">Returns matching teams with seat and member counts.</response>
    [HttpGet]
    [ProducesResponseType(typeof(List<TeamSearchResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<TeamSearchResult>>> Search([FromQuery] string? q)
    {
        var query = _db.Teams.AsQueryable();
        if (!string.IsNullOrWhiteSpace(q))
            query = query.Where(t => t.Name.Contains(q));

        var teams = await query
            .Select(t => new TeamSearchResult(
                t.Id,
                t.Name,
                t.Seats.Count,
                t.Reportees.Count(r => r.IsApproved)))
            .ToListAsync();

        return Ok(teams);
    }

    /// <summary>
    /// Get team details (public)
    /// </summary>
    /// <param name="id">The team ID.</param>
    /// <response code="200">Returns team details.</response>
    /// <response code="404">Team not found.</response>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(TeamResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TeamResponse>> GetById(int id)
    {
        var team = await _db.Teams.FindAsync(id);
        if (team == null) return NotFound(new { error = "Team not found" });
        return Ok(new TeamResponse(team.Id, team.Name, team.ManagerTotpSecret != null));
    }

    /// <summary>
    /// Create a new team with TOTP setup (atomic).
    /// Client provides a secret key + TOTP code; if valid, both team and TOTP are created together.
    /// </summary>
    /// <param name="request">Team name (optional), secret key, and TOTP code.</param>
    /// <response code="201">Team created successfully.</response>
    /// <response code="400">Missing secret key / TOTP code, or invalid TOTP code.</response>
    /// <response code="409">Team name already taken.</response>
    [HttpPost]
    [ProducesResponseType(typeof(TeamResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<TeamResponse>> Create([FromBody] CreateTeamRequest request)
    {
        // Validate TOTP first — fail fast before creating anything
        if (string.IsNullOrWhiteSpace(request.SecretKey) || string.IsNullOrWhiteSpace(request.TotpCode))
            return BadRequest(new { error = "SecretKey and TotpCode are required for team creation" });

        if (!_totpService.ValidateTotp(request.SecretKey, request.TotpCode))
            return BadRequest(new { error = "TOTP code does not match the secret key. Try again." });

        if (!string.IsNullOrWhiteSpace(request.Name) && await _db.Teams.AnyAsync(t => t.Name == request.Name))
            return Conflict(new { error = "Team name already taken" });

        var team = new Team { ManagerTotpSecret = request.SecretKey };

        if (!string.IsNullOrWhiteSpace(request.Name))
            team.Name = request.Name;

        _db.Teams.Add(team);
        await _db.SaveChangesAsync();

        // If no name provided, generate "team-{id}"
        if (string.IsNullOrWhiteSpace(team.Name))
        {
            team.Name = $"team-{team.Id}";
            await _db.SaveChangesAsync();
        }

        return CreatedAtAction(nameof(GetById), new { id = team.Id },
            new TeamResponse(team.Id, team.Name, true));
    }

    /// <summary>
    /// Delete a team and all its members, seats, and bookings (manager TOTP required).
    /// Authorization: TOTP manager:{teamId}:{code}
    /// </summary>
    /// <param name="id">The team ID to delete.</param>
    /// <response code="200">Team deleted. Returns counts of removed bookings, members, and seats.</response>
    /// <response code="403">TOTP auth does not match the team manager.</response>
    /// <response code="404">Team not found.</response>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [TotpAuth]
    public async Task<ActionResult> Delete(int id)
    {
        var authId = (int)HttpContext.Items["TotpEntityId"]!;
        var authType = (string)HttpContext.Items["TotpEntityType"]!;
        if (authType != "manager" || authId != id)
            return Forbid();

        var team = await _db.Teams.FindAsync(id);
        if (team == null) return NotFound(new { error = "Team not found" });

        // Delete all bookings for this team first (FK restrict)
        var bookings = await _db.Bookings.Where(b => b.TeamId == id).ToListAsync();
        _db.Bookings.RemoveRange(bookings);

        // Delete all reportees
        var reportees = await _db.Reportees.Where(r => r.TeamId == id).ToListAsync();
        _db.Reportees.RemoveRange(reportees);

        // Delete all seats
        var seats = await _db.Seats.Where(s => s.TeamId == id).ToListAsync();
        _db.Seats.RemoveRange(seats);

        // Delete the team
        _db.Teams.Remove(team);

        await _db.SaveChangesAsync();

        return Ok(new { message = "Team deleted", bookingsRemoved = bookings.Count, membersRemoved = reportees.Count, seatsRemoved = seats.Count });
    }
}

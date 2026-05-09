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
    [HttpGet]
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
    [HttpGet("{id}")]
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
    [HttpPost]
    public async Task<ActionResult<TeamResponse>> Create([FromBody] CreateTeamRequest request)
    {
        // Validate TOTP first — fail fast before creating anything
        if (string.IsNullOrWhiteSpace(request.SecretKey) || string.IsNullOrWhiteSpace(request.TotpCode))
            return BadRequest(new { error = "SecretKey and TotpCode are required for team creation" });

        if (!_totpService.ValidateTotp(request.SecretKey, request.TotpCode))
            return BadRequest(new { error = "TOTP code does not match the secret key. Make sure your authenticator is synced." });

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
}

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
    /// Create a new team (public - TOTP setup is a separate step)
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<TeamResponse>> Create([FromBody] CreateTeamRequest request)
    {
        var team = new Team();

        if (!string.IsNullOrWhiteSpace(request.Name))
        {
            if (await _db.Teams.AnyAsync(t => t.Name == request.Name))
                return Conflict(new { error = "Team name already taken" });
            team.Name = request.Name;
        }

        _db.Teams.Add(team);
        await _db.SaveChangesAsync();

        // If no name provided, generate "team-{id}"
        if (string.IsNullOrWhiteSpace(team.Name))
        {
            team.Name = $"team-{team.Id}";
            await _db.SaveChangesAsync();
        }

        return CreatedAtAction(nameof(GetById), new { id = team.Id },
            new TeamResponse(team.Id, team.Name, false));
    }

    /// <summary>
    /// Setup TOTP for the team manager.
    /// Client provides a secret key and a TOTP code generated from that secret.
    /// If the code validates against the secret, we store the secret.
    /// </summary>
    [HttpPost("{id}/setup-totp")]
    public async Task<ActionResult<TotpSetupResponse>> SetupTotp(int id, [FromBody] TotpSetupRequest request)
    {
        var team = await _db.Teams.FindAsync(id);
        if (team == null) return NotFound(new { error = "Team not found" });

        if (team.ManagerTotpSecret != null)
            return BadRequest(new { error = "TOTP already set up for this team" });

        // Validate the provided code against the provided secret
        if (!_totpService.ValidateTotp(request.SecretKey, request.TotpCode))
            return BadRequest(new TotpSetupResponse(false, "TOTP code does not match the secret key. Make sure your authenticator is synced."));

        team.ManagerTotpSecret = request.SecretKey;
        await _db.SaveChangesAsync();

        return Ok(new TotpSetupResponse(true, "TOTP setup successful for team manager"));
    }

    /// <summary>
    /// Generate a new TOTP secret (helper endpoint for clients)
    /// </summary>
    [HttpGet("generate-secret")]
    public ActionResult<object> GenerateSecret()
    {
        var secret = _totpService.GenerateSecret();
        return Ok(new { secretKey = secret, message = "Add this to your authenticator app, then call setup-totp with the code" });
    }
}

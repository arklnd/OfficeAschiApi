using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OfficeAschiApi.Data;
using OfficeAschiApi.DTOs;
using OfficeAschiApi.Middleware;
using OfficeAschiApi.Models;

namespace OfficeAschiApi.Controllers;

[ApiController]
[Route("api/teams/{teamId}/[controller]")]
public class SeatsController : ControllerBase
{
    private readonly AppDbContext _db;

    public SeatsController(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// List seats in a team (public)
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<SeatResponse>>> List(int teamId)
    {
        if (!await _db.Teams.AnyAsync(t => t.Id == teamId))
            return NotFound(new { error = "Team not found" });

        var seats = await _db.Seats
            .Where(s => s.TeamId == teamId)
            .Select(s => new SeatResponse(s.Id, s.Label, s.TeamId))
            .ToListAsync();

        return Ok(seats);
    }

    /// <summary>
    /// Add a seat to the team (manager TOTP required)
    /// Authorization: TOTP manager:{teamId}:{code}
    /// </summary>
    [HttpPost]
    [TotpAuth]
    public async Task<ActionResult<SeatResponse>> Add(int teamId, [FromBody] AddSeatRequest request)
    {
        // Verify the authenticated manager owns this team
        var authId = (int)HttpContext.Items["TotpEntityId"]!;
        var authType = (string)HttpContext.Items["TotpEntityType"]!;
        if (authType != "manager" || authId != teamId)
            return Forbid();

        if (!await _db.Teams.AnyAsync(t => t.Id == teamId))
            return NotFound(new { error = "Team not found" });

        if (string.IsNullOrWhiteSpace(request.Label))
            return BadRequest(new { error = "Seat label is required" });

        var seat = new Seat { TeamId = teamId, Label = request.Label };
        _db.Seats.Add(seat);
        await _db.SaveChangesAsync();

        return CreatedAtAction(nameof(List), new { teamId },
            new SeatResponse(seat.Id, seat.Label, seat.TeamId));
    }

    /// <summary>
    /// Delete a seat from the team (manager TOTP required).
    /// Fails if the seat has any confirmed bookings.
    /// Authorization: TOTP manager:{teamId}:{code}
    /// </summary>
    [HttpDelete("{seatId}")]
    [TotpAuth]
    public async Task<ActionResult> Delete(int teamId, int seatId)
    {
        var authId = (int)HttpContext.Items["TotpEntityId"]!;
        var authType = (string)HttpContext.Items["TotpEntityType"]!;
        if (authType != "manager" || authId != teamId)
            return Forbid();

        var seat = await _db.Seats.FirstOrDefaultAsync(s => s.Id == seatId && s.TeamId == teamId);
        if (seat == null)
            return NotFound(new { error = "Seat not found in this team" });

        var hasBookings = await _db.Bookings.AnyAsync(b => b.SeatId == seatId);
        if (hasBookings)
            return BadRequest(new { error = "Cannot delete seat with existing bookings. Cancel all bookings first." });

        _db.Seats.Remove(seat);
        await _db.SaveChangesAsync();

        return Ok(new { message = "Seat deleted" });
    }
}

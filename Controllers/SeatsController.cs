using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OfficeAschiApi.Caching;
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
    private readonly WriteThroughCache _writeThroughCache;

    public SeatsController(AppDbContext db, WriteThroughCache writeThroughCache)
    {
        _db = db;
        _writeThroughCache = writeThroughCache;
    }

    /// <summary>
    /// List seats in a team (public)
    /// </summary>
    /// <param name="teamId">The ID of the team.</param>
    /// <response code="200">Returns the list of seats in the team.</response>
    /// <response code="404">Team not found.</response>
    [HttpGet]
    [ProducesResponseType(typeof(List<SeatResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<List<SeatResponse>> List(int teamId)
    {
        if (!_writeThroughCache.TeamExists(teamId))
            return NotFound(new { error = "Team not found" });

        var seats = _writeThroughCache.GetSeatsByTeam(teamId)
            .Select(s => new SeatResponse(s.Id, s.Label, s.TeamId))
            .ToList();

        return Ok(seats);
    }

    /// <summary>
    /// Add a seat to the team (manager TOTP required)
    /// Authorization: TOTP manager:{teamId}:{code}
    /// </summary>
    /// <param name="teamId">The ID of the team.</param>
    /// <param name="request">The seat label.</param>
    /// <response code="201">Seat created successfully.</response>
    /// <response code="400">Seat label is required.</response>
    /// <response code="403">TOTP auth does not match the team manager.</response>
    /// <response code="404">Team not found.</response>
    [HttpPost]
    [ProducesResponseType(typeof(SeatResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [TotpAuth]
    public async Task<ActionResult<SeatResponse>> Add(int teamId, [FromBody] AddSeatRequest request)
    {
        // Verify the authenticated manager owns this team
        var authId = (int)HttpContext.Items["TotpEntityId"]!;
        var authType = (string)HttpContext.Items["TotpEntityType"]!;
        if (authType != "manager" || authId != teamId)
            return Forbid();

        if (!_writeThroughCache.TeamExists(teamId))
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
    /// <param name="teamId">The ID of the team.</param>
    /// <param name="seatId">The ID of the seat to delete.</param>
    /// <response code="200">Seat deleted successfully.</response>
    /// <response code="400">Cannot delete seat with existing bookings.</response>
    /// <response code="403">TOTP auth does not match the team manager.</response>
    /// <response code="404">Seat not found in this team.</response>
    [HttpDelete("{seatId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
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

    /// <summary>
    /// Get all seats across all teams with occupancy status for a given date.
    /// </summary>
    /// <param name="date">The date to check (defaults to today).</param>
    /// <response code="200">Returns all seats with their occupancy status.</response>
    [HttpGet("/api/Seats")]
    [ProducesResponseType(typeof(List<SeatOverviewResponse>), StatusCodes.Status200OK)]
    public ActionResult<List<SeatOverviewResponse>> GetAll([FromQuery] DateOnly? date)
    {
        var targetDate = date ?? DateOnly.FromDateTime(DateTime.UtcNow);

        var result = _writeThroughCache.GetAllSeats()
            .Select(s =>
            {
                var team = _writeThroughCache.GetTeam(s.TeamId);
                var confirmedBooking = _writeThroughCache.GetBookingsByTeamAndDate(s.TeamId, targetDate)
                    .Where(b => b.SeatId == s.Id && b.Status == BookingStatus.Confirmed)
                    .Select(b =>
                    {
                        var reportee = _writeThroughCache.GetReportee(b.ReporteeId);
                        return new SeatOverviewBooking(
                            b.ReporteeId,
                            reportee?.FriendlyName ?? "",
                            b.Id,
                            b.Status.ToString(),
                            b.CreatedAt);
                    })
                    .FirstOrDefault();

                return new SeatOverviewResponse(
                    s.Id, s.Label, s.TeamId,
                    team?.Name ?? "",
                    confirmedBooking != null,
                    confirmedBooking);
            })
            .OrderBy(s => s.TeamName)
            .ThenBy(s => s.Label)
            .ToList();

        return Ok(result);
    }
}

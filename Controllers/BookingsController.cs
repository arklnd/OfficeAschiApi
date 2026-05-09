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
public class BookingsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly WaitlistService _waitlistService;

    public BookingsController(AppDbContext db, WaitlistService waitlistService)
    {
        _db = db;
        _waitlistService = waitlistService;
    }

    /// <summary>
    /// Get availability for a team on a date (public)
    /// </summary>
    [HttpGet("availability/{teamId}")]
    public async Task<ActionResult<AvailabilityResponse>> Availability(int teamId, [FromQuery] DateOnly date)
    {
        if (!await _db.Teams.AnyAsync(t => t.Id == teamId))
            return NotFound(new { error = "Team not found" });

        var allSeats = await _db.Seats.Where(s => s.TeamId == teamId).ToListAsync();
        var allSeatIds = allSeats.Select(s => s.Id).ToHashSet();

        var bookings = await _db.Bookings
            .Include(b => b.Seat)
            .Include(b => b.Reportee)
            .Where(b => b.TeamId == teamId && b.Date == date)
            .OrderBy(b => b.CreatedAt)
            .ToListAsync();

        var confirmed = bookings.Where(b => b.Status == BookingStatus.Confirmed).ToList();
        var waitlisted = bookings.Where(b => b.Status == BookingStatus.Waitlisted).ToList();
        var bookedSeatIds = confirmed.Select(b => b.SeatId).ToHashSet();

        var availableSeats = allSeats
            .Where(s => !bookedSeatIds.Contains(s.Id))
            .Select(s => new SeatResponse(s.Id, s.Label, s.TeamId))
            .ToList();

        var bookingResponses = confirmed.Select(b => new BookingResponse(
            b.Id, b.Date, b.SeatId, b.Seat.Label, b.ReporteeId,
            b.Reportee.FriendlyName, "Confirmed", b.CreatedAt)).ToList();

        var waitlistInfos = waitlisted.Select(b => new WaitlistInfo(
            b.Id, b.Reportee.FriendlyName, b.Seat.Label, b.CreatedAt)).ToList();

        return Ok(new AvailabilityResponse(
            date,
            allSeats.Count,
            confirmed.Count,
            availableSeats.Count,
            waitlisted.Count,
            bookingResponses,
            availableSeats,
            waitlistInfos));
    }

    /// <summary>
    /// Book a seat (reportee TOTP required).
    /// If the seat is already taken, the booking becomes waitlisted for that seat.
    /// Authorization: TOTP reportee:{reporteeId}:{code}
    /// </summary>
    [HttpPost]
    [TotpAuth]
    public async Task<ActionResult<BookingResponse>> Book([FromBody] BookSeatRequest request)
    {
        // Verify auth matches the reportee
        var authId = (int)HttpContext.Items["TotpEntityId"]!;
        var authType = (string)HttpContext.Items["TotpEntityType"]!;
        if (authType != "reportee" || authId != request.ReporteeId)
            return Forbid();

        var reportee = await _db.Reportees.FindAsync(request.ReporteeId);
        if (reportee == null) return NotFound(new { error = "Reportee not found" });
        if (!reportee.IsApproved) return BadRequest(new { error = "Reportee not approved by manager yet" });

        var seat = await _db.Seats.FindAsync(request.SeatId);
        if (seat == null) return NotFound(new { error = "Seat not found" });
        if (seat.TeamId != reportee.TeamId)
            return BadRequest(new { error = "Seat does not belong to your team" });

        // Check if reportee already has a booking for this date
        var existing = await _db.Bookings.FirstOrDefaultAsync(b =>
            b.ReporteeId == request.ReporteeId && b.Date == request.Date);
        if (existing != null)
            return Conflict(new { error = "You already have a booking for this date", status = existing.Status.ToString() });

        // Check if seat is available (no confirmed booking)
        var seatTaken = await _db.Bookings.AnyAsync(b =>
            b.SeatId == request.SeatId && b.Date == request.Date && b.Status == BookingStatus.Confirmed);

        // Check if ALL seats in the team are taken for this date
        var totalSeats = await _db.Seats.CountAsync(s => s.TeamId == reportee.TeamId);
        var confirmedCount = await _db.Bookings.CountAsync(b =>
            b.TeamId == reportee.TeamId && b.Date == request.Date && b.Status == BookingStatus.Confirmed);

        BookingStatus status;
        if (!seatTaken)
        {
            status = BookingStatus.Confirmed;
        }
        else if (confirmedCount >= totalSeats)
        {
            // All seats full — waitlist for the desired seat
            status = BookingStatus.Waitlisted;
        }
        else
        {
            // The specific seat is taken but others are available
            return BadRequest(new { error = "This seat is taken. Other seats are available — pick a different one." });
        }

        var booking = new Booking
        {
            Date = request.Date,
            SeatId = request.SeatId,
            ReporteeId = request.ReporteeId,
            TeamId = reportee.TeamId,
            Status = status,
            CreatedAt = DateTime.UtcNow
        };

        _db.Bookings.Add(booking);
        await _db.SaveChangesAsync();

        return CreatedAtAction(nameof(Availability), new { teamId = reportee.TeamId, date = request.Date },
            new BookingResponse(booking.Id, booking.Date, booking.SeatId,
                seat.Label, booking.ReporteeId, reportee.FriendlyName,
                status.ToString(), booking.CreatedAt));
    }

    /// <summary>
    /// Cancel a booking (reportee TOTP required).
    /// When a confirmed booking is cancelled, waitlisted candidates are auto-promoted.
    /// Authorization: TOTP reportee:{reporteeId}:{code}
    /// </summary>
    [HttpDelete("{id}")]
    [TotpAuth]
    public async Task<ActionResult> Cancel(int id)
    {
        var booking = await _db.Bookings
            .Include(b => b.Seat)
            .Include(b => b.Reportee)
            .FirstOrDefaultAsync(b => b.Id == id);

        if (booking == null) return NotFound(new { error = "Booking not found" });

        // Verify auth matches the reportee
        var authId = (int)HttpContext.Items["TotpEntityId"]!;
        var authType = (string)HttpContext.Items["TotpEntityType"]!;
        if (authType != "reportee" || authId != booking.ReporteeId)
            return Forbid();

        var wasConfirmed = booking.Status == BookingStatus.Confirmed;
        var vacatedSeatId = booking.SeatId;
        var teamId = booking.TeamId;
        var date = booking.Date;

        _db.Bookings.Remove(booking);
        await _db.SaveChangesAsync();

        // If a confirmed booking was cancelled, promote from waitlist
        if (wasConfirmed)
        {
            await _waitlistService.PromoteWaitlistAsync(teamId, vacatedSeatId, date);
        }

        return Ok(new { message = "Booking cancelled", wasConfirmed, promotedFromWaitlist = wasConfirmed });
    }
}

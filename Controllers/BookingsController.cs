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
    /// <param name="teamId">The ID of the team to check availability for.</param>
    /// <param name="date">The date to check availability on.</param>
    /// <response code="200">Returns seat availability, confirmed bookings, and waitlist info.</response>
    /// <response code="404">Team not found.</response>
    [HttpGet("availability/{teamId}")]
    [ProducesResponseType(typeof(AvailabilityResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
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
            b.Reportee.FriendlyName, "Confirmed", DateTime.SpecifyKind(b.CreatedAt, DateTimeKind.Utc))).ToList();

        var waitlistInfos = waitlisted.Select(b => new WaitlistInfo(
            b.Id, b.Reportee.FriendlyName, b.Seat.Label, DateTime.SpecifyKind(b.CreatedAt, DateTimeKind.Utc))).ToList();

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
    /// Get availability for a team across a date range (public).
    /// Returns a compact summary per day — ideal for checking a full month in one call.
    /// </summary>
    /// <param name="teamId">The ID of the team to check availability for.</param>
    /// <param name="from">Start date (inclusive).</param>
    /// <param name="to">End date (inclusive). Max 90 days from start.</param>
    /// <response code="200">Returns per-day availability summary for the date range.</response>
    /// <response code="400">Invalid date range.</response>
    /// <response code="404">Team not found.</response>
    [HttpGet("availability/{teamId}/range")]
    [ProducesResponseType(typeof(RangeAvailabilityResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RangeAvailabilityResponse>> AvailabilityRange(
        int teamId, [FromQuery] DateOnly from, [FromQuery] DateOnly to)
    {
        if (to < from)
            return BadRequest(new { error = "to must be >= from" });
        if (to.DayNumber - from.DayNumber > 90)
            return BadRequest(new { error = "Date range cannot exceed 90 days" });

        if (!await _db.Teams.AnyAsync(t => t.Id == teamId))
            return NotFound(new { error = "Team not found" });

        var totalSeats = await _db.Seats.CountAsync(s => s.TeamId == teamId);

        var bookings = await _db.Bookings
            .Where(b => b.TeamId == teamId && b.Date >= from && b.Date <= to)
            .GroupBy(b => b.Date)
            .Select(g => new
            {
                Date = g.Key,
                Confirmed = g.Count(b => b.Status == BookingStatus.Confirmed),
                Waitlisted = g.Count(b => b.Status == BookingStatus.Waitlisted)
            })
            .ToListAsync();

        var bookingsByDate = bookings.ToDictionary(b => b.Date);

        var days = new List<DateAvailabilitySummary>();
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            if (bookingsByDate.TryGetValue(d, out var b))
            {
                days.Add(new DateAvailabilitySummary(d, totalSeats, b.Confirmed,
                    totalSeats - b.Confirmed, b.Waitlisted));
            }
            else
            {
                days.Add(new DateAvailabilitySummary(d, totalSeats, 0, totalSeats, 0));
            }
        }

        return Ok(new RangeAvailabilityResponse(teamId, from, to, days));
    }

    /// <summary>
    /// Book a seat for a date range (reportee TOTP required).
    /// Creates one booking per day. Days where the seat is taken are waitlisted; days with prior bookings are skipped.
    /// Authorization: TOTP reportee:{reporteeId}:{code}
    /// </summary>
    /// <param name="request">Reportee ID, seat ID, from date, and to date (inclusive). Max 90 days.</param>
    /// <response code="201">Range booking results per day.</response>
    /// <response code="400">Invalid range, reportee not approved, or seat belongs to another team.</response>
    /// <response code="403">TOTP auth does not match the reportee.</response>
    /// <response code="404">Reportee or seat not found.</response>
    [HttpPost("range")]
    [ProducesResponseType(typeof(RangeBookingResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [TotpAuth]
    public async Task<ActionResult<RangeBookingResponse>> BookRange([FromBody] BookSeatRangeRequest request)
    {
        if (request.To < request.From)
            return BadRequest(new { error = "to must be >= from" });
        if (request.To.DayNumber - request.From.DayNumber > 90)
            return BadRequest(new { error = "Date range cannot exceed 90 days" });

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

        var totalSeats = await _db.Seats.CountAsync(s => s.TeamId == reportee.TeamId);

        // Fetch existing bookings for the reportee and seat in the date range in bulk
        var existingReporteeBookings = await _db.Bookings
            .Where(b => b.ReporteeId == request.ReporteeId && b.Date >= request.From && b.Date <= request.To)
            .Select(b => b.Date)
            .ToHashSetAsync();

        var confirmedSeatDates = await _db.Bookings
            .Where(b => b.SeatId == request.SeatId && b.Date >= request.From && b.Date <= request.To
                && b.Status == BookingStatus.Confirmed)
            .Select(b => b.Date)
            .ToHashSetAsync();

        var confirmedTeamCounts = await _db.Bookings
            .Where(b => b.TeamId == reportee.TeamId && b.Date >= request.From && b.Date <= request.To
                && b.Status == BookingStatus.Confirmed)
            .GroupBy(b => b.Date)
            .Select(g => new { Date = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Date, x => x.Count);

        var results = new List<RangeBookingResult>();
        var newBookings = new List<Booking>();

        for (var d = request.From; d <= request.To; d = d.AddDays(1))
        {
            if (existingReporteeBookings.Contains(d))
            {
                results.Add(new RangeBookingResult(d, false, "Skipped", null,
                    $"You already have a booking on {d:yyyy-MM-dd} — cancel it first to rebook"));
                continue;
            }

            var seatTaken = confirmedSeatDates.Contains(d);
            var teamConfirmed = confirmedTeamCounts.GetValueOrDefault(d, 0);

            BookingStatus status;
            if (!seatTaken)
            {
                status = BookingStatus.Confirmed;
            }
            else if (teamConfirmed >= totalSeats)
            {
                status = BookingStatus.Waitlisted;
            }
            else
            {
                var available = totalSeats - teamConfirmed;
                results.Add(new RangeBookingResult(d, false, "Skipped", null,
                    $"{seat.Label} is taken on {d:yyyy-MM-dd} but {available} other seat{(available == 1 ? " is" : "s are")} available — pick a different seat for this date"));
                continue;
            }

            var booking = new Booking
            {
                Date = d,
                SeatId = request.SeatId,
                ReporteeId = request.ReporteeId,
                TeamId = reportee.TeamId,
                Status = status,
                CreatedAt = DateTime.UtcNow
            };
            newBookings.Add(booking);

            // Update in-memory counts so subsequent days are consistent
            if (status == BookingStatus.Confirmed)
            {
                confirmedSeatDates.Add(d);
                confirmedTeamCounts[d] = teamConfirmed + 1;
            }
            existingReporteeBookings.Add(d);
        }

        if (newBookings.Count > 0)
        {
            _db.Bookings.AddRange(newBookings);
            await _db.SaveChangesAsync();
        }

        // Build results for created bookings
        foreach (var b in newBookings)
        {
            results.Add(new RangeBookingResult(b.Date, true, b.Status.ToString(), b.Id, null));
        }

        // Sort by date
        results = results.OrderBy(r => r.Date).ToList();

        return StatusCode(StatusCodes.Status201Created, new RangeBookingResponse(
            seat.Id, seat.Label, reportee.Id, reportee.FriendlyName,
            request.From, request.To,
            newBookings.Count(b => b.Status == BookingStatus.Confirmed),
            newBookings.Count(b => b.Status == BookingStatus.Waitlisted),
            results.Count(r => !r.Success),
            results));
    }

    /// <summary>
    /// Book a seat (reportee TOTP required).
    /// If the seat is already taken, the booking becomes waitlisted for that seat.
    /// Authorization: TOTP reportee:{reporteeId}:{code}
    /// </summary>
    /// <param name="request">The booking details including reportee ID, seat ID, and date.</param>
    /// <response code="201">Booking created (confirmed or waitlisted).</response>
    /// <response code="400">Reportee not approved, seat belongs to another team, or specific seat taken with others available.</response>
    /// <response code="403">TOTP auth does not match the reportee.</response>
    /// <response code="404">Reportee or seat not found.</response>
    /// <response code="409">Reportee already has a booking for this date.</response>
    [HttpPost]
    [ProducesResponseType(typeof(BookingResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
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
                status.ToString(), DateTime.SpecifyKind(booking.CreatedAt, DateTimeKind.Utc)));
    }

    /// <summary>
    /// Cancel a booking (reportee TOTP required).
    /// When a confirmed booking is cancelled, waitlisted candidates are auto-promoted.
    /// Authorization: TOTP reportee:{reporteeId}:{code}
    /// </summary>
    /// <param name="id">The booking ID to cancel.</param>
    /// <response code="200">Booking cancelled successfully. Indicates if a waitlisted entry was promoted.</response>
    /// <response code="403">TOTP auth does not match the booking's reportee.</response>
    /// <response code="404">Booking not found.</response>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
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

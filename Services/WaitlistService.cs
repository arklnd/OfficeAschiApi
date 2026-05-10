using Microsoft.EntityFrameworkCore;
using OfficeAschiApi.Data;
using OfficeAschiApi.Models;

namespace OfficeAschiApi.Services;

public class WaitlistService
{
    private readonly AppDbContext _db;

    public WaitlistService(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// When a seat is vacated (booking cancelled), promote the next waitlisted person.
    /// Logic:
    /// 1. First check if anyone is waitlisted specifically for THIS seat → promote earliest
    /// 2. If no one, find the globally earliest waitlisted person in the team for that date → give them this seat
    /// </summary>
    public async Task PromoteWaitlistAsync(int teamId, int vacatedSeatId, DateOnly date)
    {
        // 1. Check waitlist for this specific seat
        var nextForSeat = await _db.Bookings
            .Where(b => b.TeamId == teamId
                && b.Date == date
                && b.Status == BookingStatus.Waitlisted
                && b.SeatId == vacatedSeatId)
            .OrderBy(b => b.CreatedAt)
            .FirstOrDefaultAsync();

        if (nextForSeat != null)
        {
            nextForSeat.Status = BookingStatus.Confirmed;
            // SeatId is already the vacated seat
            await _db.SaveChangesAsync();
            return;
        }

        // 2. Find globally earliest waitlisted person in the team for this date
        var nextGlobal = await _db.Bookings
            .Where(b => b.TeamId == teamId
                && b.Date == date
                && b.Status == BookingStatus.Waitlisted)
            .OrderBy(b => b.CreatedAt)
            .FirstOrDefaultAsync();

        if (nextGlobal != null)
        {
            nextGlobal.Status = BookingStatus.Confirmed;
            nextGlobal.SeatId = vacatedSeatId; // assign the vacated seat
            await _db.SaveChangesAsync();
        }
    }
}

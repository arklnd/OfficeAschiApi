using System.Collections.Concurrent;
using OfficeAschiApi.Models;

namespace OfficeAschiApi.Caching;

/// <summary>
/// Write-through in-memory cache that mirrors the database.
/// Reads are served from RAM; writes go to DB first (by the caller), then update the cache.
/// On app restart, <see cref="CacheWarmupService"/> reloads everything from DB.
///
/// Bookings older than 90 days are pruned daily to bound memory growth.
/// </summary>
public sealed class WriteThroughCache
{
    // ── Primary stores (entity Id → entity) ──
    private readonly ConcurrentDictionary<int, Team> _teams = new();
    private readonly ConcurrentDictionary<int, Seat> _seats = new();
    private readonly ConcurrentDictionary<int, Reportee> _reportees = new();
    private readonly ConcurrentDictionary<int, Booking> _bookings = new();

    // ── Secondary indexes ──
    // ConcurrentDictionary<int, byte> is used as a concurrent HashSet<int>
    private readonly ConcurrentDictionary<int, ConcurrentDictionary<int, byte>> _seatIdsByTeam = new();
    private readonly ConcurrentDictionary<int, ConcurrentDictionary<int, byte>> _reporteeIdsByTeam = new();
    private readonly ConcurrentDictionary<(int TeamId, DateOnly Date), ConcurrentDictionary<int, byte>> _bookingIdsByTeamDate = new();
    private readonly ConcurrentDictionary<int, ConcurrentDictionary<int, byte>> _bookingIdsByReportee = new();

    public bool IsWarmedUp { get; private set; }

    // ══════════════════════════════════════════
    //  WARMUP
    // ══════════════════════════════════════════

    public void WarmUp(List<Team> teams, List<Seat> seats, List<Reportee> reportees, List<Booking> bookings)
    {
        foreach (var t in teams) _teams[t.Id] = t;
        foreach (var s in seats) { _seats[s.Id] = s; AddToIndex(_seatIdsByTeam, s.TeamId, s.Id); }
        foreach (var r in reportees) { _reportees[r.Id] = r; AddToIndex(_reporteeIdsByTeam, r.TeamId, r.Id); }
        foreach (var b in bookings) { _bookings[b.Id] = b; IndexBooking(b); }
        IsWarmedUp = true;
    }

    // ══════════════════════════════════════════
    //  TEAM — reads
    // ══════════════════════════════════════════

    public Team? GetTeam(int id) => _teams.GetValueOrDefault(id);
    public bool TeamExists(int id) => _teams.ContainsKey(id);
    public ICollection<Team> GetAllTeams() => _teams.Values;

    public int GetSeatCount(int teamId) =>
        _seatIdsByTeam.TryGetValue(teamId, out var set) ? set.Count : 0;

    public int GetApprovedReporteeCount(int teamId)
    {
        if (!_reporteeIdsByTeam.TryGetValue(teamId, out var set)) return 0;
        var count = 0;
        foreach (var rid in set.Keys)
            if (_reportees.TryGetValue(rid, out var r) && r.IsApproved) count++;
        return count;
    }

    // ── TEAM — writes ──

    public void PutTeam(Team team) => _teams[team.Id] = team;

    public void RemoveTeam(int id) => _teams.TryRemove(id, out _);

    // ══════════════════════════════════════════
    //  SEAT — reads
    // ══════════════════════════════════════════

    public Seat? GetSeat(int id) => _seats.GetValueOrDefault(id);
    public ICollection<Seat> GetAllSeats() => _seats.Values;

    public List<Seat> GetSeatsByTeam(int teamId)
    {
        if (!_seatIdsByTeam.TryGetValue(teamId, out var set)) return [];
        var list = new List<Seat>(set.Count);
        foreach (var sid in set.Keys)
            if (_seats.TryGetValue(sid, out var s)) list.Add(s);
        return list;
    }

    // ── SEAT — writes ──

    public void PutSeat(Seat seat)
    {
        _seats[seat.Id] = seat;
        AddToIndex(_seatIdsByTeam, seat.TeamId, seat.Id);
    }

    public void RemoveSeat(int seatId)
    {
        if (_seats.TryRemove(seatId, out var seat))
            RemoveFromIndex(_seatIdsByTeam, seat.TeamId, seatId);
    }

    // ══════════════════════════════════════════
    //  REPORTEE — reads
    // ══════════════════════════════════════════

    public Reportee? GetReportee(int id) => _reportees.GetValueOrDefault(id);

    public List<Reportee> GetReporteesByTeam(int teamId)
    {
        if (!_reporteeIdsByTeam.TryGetValue(teamId, out var set)) return [];
        var list = new List<Reportee>(set.Count);
        foreach (var rid in set.Keys)
            if (_reportees.TryGetValue(rid, out var r)) list.Add(r);
        return list;
    }

    // ── REPORTEE — writes ──

    public void PutReportee(Reportee reportee)
    {
        _reportees[reportee.Id] = reportee;
        AddToIndex(_reporteeIdsByTeam, reportee.TeamId, reportee.Id);
    }

    public void RemoveReportee(int reporteeId)
    {
        if (_reportees.TryRemove(reporteeId, out var r))
            RemoveFromIndex(_reporteeIdsByTeam, r.TeamId, reporteeId);
    }

    // ══════════════════════════════════════════
    //  BOOKING — reads
    // ══════════════════════════════════════════

    public Booking? GetBooking(int id) => _bookings.GetValueOrDefault(id);

    public List<Booking> GetBookingsByTeamAndDate(int teamId, DateOnly date)
    {
        if (!_bookingIdsByTeamDate.TryGetValue((teamId, date), out var set)) return [];
        var list = new List<Booking>(set.Count);
        foreach (var bid in set.Keys)
            if (_bookings.TryGetValue(bid, out var b)) list.Add(b);
        return list;
    }

    public List<Booking> GetBookingsByTeamInRange(int teamId, DateOnly from, DateOnly to)
    {
        var result = new List<Booking>();
        for (var d = from; d <= to; d = d.AddDays(1))
            result.AddRange(GetBookingsByTeamAndDate(teamId, d));
        return result;
    }

    public List<Booking> GetBookingsByReportee(int reporteeId)
    {
        if (!_bookingIdsByReportee.TryGetValue(reporteeId, out var set)) return [];
        var list = new List<Booking>(set.Count);
        foreach (var bid in set.Keys)
            if (_bookings.TryGetValue(bid, out var b)) list.Add(b);
        return list;
    }

    public Booking? GetReporteeBookingOnDate(int reporteeId, DateOnly date)
    {
        if (!_bookingIdsByReportee.TryGetValue(reporteeId, out var set)) return null;
        foreach (var bid in set.Keys)
            if (_bookings.TryGetValue(bid, out var b) && b.Date == date) return b;
        return null;
    }

    public int GetConfirmedCountByTeamAndDate(int teamId, DateOnly date) =>
        GetBookingsByTeamAndDate(teamId, date).Count(b => b.Status == BookingStatus.Confirmed);

    // ── BOOKING — writes ──

    public void PutBooking(Booking booking)
    {
        _bookings[booking.Id] = booking;
        IndexBooking(booking);
    }

    public void RemoveBooking(int bookingId)
    {
        if (_bookings.TryRemove(bookingId, out var b))
            UnindexBooking(b);
    }

    // ══════════════════════════════════════════
    //  MAINTENANCE
    // ══════════════════════════════════════════

    /// <summary>Remove bookings older than <paramref name="cutoff"/> to bound memory.</summary>
    public int PruneBookingsBefore(DateOnly cutoff)
    {
        var toRemove = new List<int>();
        foreach (var b in _bookings.Values)
            if (b.Date < cutoff) toRemove.Add(b.Id);

        foreach (var id in toRemove) RemoveBooking(id);
        return toRemove.Count;
    }

    /// <summary>Diagnostic snapshot of cached entity counts.</summary>
    public (int Teams, int Seats, int Reportees, int Bookings) Counts =>
        (_teams.Count, _seats.Count, _reportees.Count, _bookings.Count);

    // ══════════════════════════════════════════
    //  INDEX HELPERS
    // ══════════════════════════════════════════

    private void IndexBooking(Booking b)
    {
        AddToIndex(_bookingIdsByTeamDate, (b.TeamId, b.Date), b.Id);
        AddToIndex(_bookingIdsByReportee, b.ReporteeId, b.Id);
    }

    private void UnindexBooking(Booking b)
    {
        RemoveFromIndex(_bookingIdsByTeamDate, (b.TeamId, b.Date), b.Id);
        RemoveFromIndex(_bookingIdsByReportee, b.ReporteeId, b.Id);
    }

    private static void AddToIndex<TKey>(
        ConcurrentDictionary<TKey, ConcurrentDictionary<int, byte>> index, TKey key, int id)
        where TKey : notnull
    {
        index.GetOrAdd(key, _ => new ConcurrentDictionary<int, byte>())[id] = 0;
    }

    private static void RemoveFromIndex<TKey>(
        ConcurrentDictionary<TKey, ConcurrentDictionary<int, byte>> index, TKey key, int id)
        where TKey : notnull
    {
        if (index.TryGetValue(key, out var set))
        {
            set.TryRemove(id, out _);
            if (set.IsEmpty) index.TryRemove(key, out _);
        }
    }
}

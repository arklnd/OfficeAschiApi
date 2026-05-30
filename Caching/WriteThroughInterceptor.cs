using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using OfficeAschiApi.Models;

namespace OfficeAschiApi.Caching;

/// <summary>
/// EF Core interceptor that automatically syncs the <see cref="WriteThroughCache"/>
/// after every successful <c>SaveChanges</c> call. Replaces manual
/// <c>_cache.Put / Remove</c> calls in controllers.
///
/// Changes are captured before save (when states are still meaningful)
/// and applied after save succeeds (when DB-generated IDs are available).
/// </summary>
public sealed class WriteThroughInterceptor : SaveChangesInterceptor
{
    private readonly WriteThroughCache _cache;

    // Stores pending changes between SavingChanges → SavedChanges per async flow
    private static readonly AsyncLocal<List<(object Entity, EntityState State)>?> _pending = new();

    public WriteThroughInterceptor(WriteThroughCache cache) => _cache = cache;

    // ── Sync path ──

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        Capture(eventData.Context!);
        return base.SavingChanges(eventData, result);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Apply();
        return base.SavedChanges(eventData, result);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        _pending.Value = null;
        base.SaveChangesFailed(eventData);
    }

    // ── Async path ──

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Capture(eventData.Context!);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result,
        CancellationToken cancellationToken = default)
    {
        Apply();
        return base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    public override Task SaveChangesFailedAsync(
        DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        _pending.Value = null;
        return base.SaveChangesFailedAsync(eventData, cancellationToken);
    }

    // ── Helpers ──

    private static void Capture(DbContext context)
    {
        _pending.Value = context.ChangeTracker.Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Where(e => e.Entity is Team or Seat or Reportee or Booking)
            .Select(e => (e.Entity, e.State))
            .ToList();
    }

    private void Apply()
    {
        var changes = _pending.Value;
        if (changes is null or { Count: 0 }) return;
        _pending.Value = null;

        foreach (var (entity, state) in changes)
        {
            switch (entity)
            {
                case Team t:
                    if (state == EntityState.Deleted) _cache.RemoveTeam(t.Id);
                    else _cache.PutTeam(t);
                    break;
                case Seat s:
                    if (state == EntityState.Deleted) _cache.RemoveSeat(s.Id);
                    else _cache.PutSeat(s);
                    break;
                case Reportee r:
                    if (state == EntityState.Deleted) _cache.RemoveReportee(r.Id);
                    else _cache.PutReportee(r);
                    break;
                case Booking b:
                    if (state == EntityState.Deleted) _cache.RemoveBooking(b.Id);
                    else _cache.PutBooking(b);
                    break;
            }
        }
    }
}

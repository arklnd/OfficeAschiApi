using Microsoft.EntityFrameworkCore;
using OfficeAschiApi.Data;

namespace OfficeAschiApi.Caching;

/// <summary>
/// Loads all data from DB into <see cref="WriteThroughCache"/> on startup
/// and prunes old bookings daily to keep memory bounded.
/// </summary>
public sealed class CacheWarmupService : IHostedService, IDisposable
{
    private readonly IServiceProvider _services;
    private readonly WriteThroughCache _cache;
    private readonly ILogger<CacheWarmupService> _logger;
    private Timer? _pruneTimer;

    public CacheWarmupService(IServiceProvider services, WriteThroughCache cache, ILogger<CacheWarmupService> logger)
    {
        _services = services;
        _cache = cache;
        _logger = logger;
    }

    private const int MaxRetries = 5;
    private static readonly int[] RetryDelaysSeconds = [2, 4, 8, 16, 32];

    public async Task StartAsync(CancellationToken ct)
    {
        // Fire-and-forget so the app starts accepting requests immediately.
        // Reads return empty results until warmup completes (IsWarmedUp guard).
        _ = Task.Run(() => WarmUpWithRetryAsync(ct), ct);
    }

    private async Task WarmUpWithRetryAsync(CancellationToken ct)
    {
        for (var attempt = 0; attempt <= MaxRetries; attempt++)
        {
            try
            {
                using var scope = _services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                var teams = await db.Teams.AsNoTracking().ToListAsync(ct);
                var seats = await db.Seats.AsNoTracking().ToListAsync(ct);
                var reportees = await db.Reportees.AsNoTracking().ToListAsync(ct);
                var bookings = await db.Bookings.AsNoTracking().ToListAsync(ct);

                _cache.WarmUp(teams, seats, reportees, bookings);

                var c = _cache.Counts;
                _logger.LogInformation(
                    "Write-through cache warmed: {Teams} teams, {Seats} seats, {Reportees} reportees, {Bookings} bookings",
                    c.Teams, c.Seats, c.Reportees, c.Bookings);

                // Prune bookings older than 90 days — run once after 1 hour, then every 24 hours
                _pruneTimer = new Timer(PruneOldBookings, null, TimeSpan.FromHours(1), TimeSpan.FromHours(24));
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                if (attempt == MaxRetries)
                {
                    _logger.LogCritical(ex, "Cache warmup failed after {Retries} retries — app will serve from DB only", MaxRetries);
                    return;
                }

                var delay = RetryDelaysSeconds[attempt];
                _logger.LogWarning(ex, "Cache warmup attempt {Attempt} failed, retrying in {Delay}s", attempt + 1, delay);
                await Task.Delay(TimeSpan.FromSeconds(delay), ct);
            }
        }
    }

    private void PruneOldBookings(object? state)
    {
        var cutoff = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-90));
        var pruned = _cache.PruneBookingsBefore(cutoff);
        if (pruned > 0)
            _logger.LogInformation("Pruned {Count} cached bookings older than {Cutoff}", pruned, cutoff);
    }

    public Task StopAsync(CancellationToken ct)
    {
        _pruneTimer?.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }

    public void Dispose() => _pruneTimer?.Dispose();
}

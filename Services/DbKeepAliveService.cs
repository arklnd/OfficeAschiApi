using Microsoft.EntityFrameworkCore;
using OfficeAschiApi.Data;

namespace OfficeAschiApi.Services;

public class DbKeepAliveService(IServiceScopeFactory scopeFactory, ILogger<DbKeepAliveService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(45);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(Interval, stoppingToken);
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await db.Database.ExecuteSqlRawAsync("SELECT 1", stoppingToken);
                logger.LogDebug("DB keep-alive ping succeeded");
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "DB keep-alive ping failed");
            }
        }
    }
}

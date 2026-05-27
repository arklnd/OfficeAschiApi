using Microsoft.EntityFrameworkCore;
using OfficeAschiApi.Data;

namespace OfficeAschiApi.Services;

public class DbKeepAliveService(IServiceScopeFactory scopeFactory, IConfiguration config, ILogger<DbKeepAliveService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(45);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(Interval, stoppingToken);
            if (!config.GetValue("DB_KEEP_ALIVE", true))
            {
                logger.LogDebug("DB keep-alive disabled via config");
                continue;
            }
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

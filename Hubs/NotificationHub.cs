using Microsoft.AspNetCore.SignalR;
using OfficeAschiApi.Services;

namespace OfficeAschiApi.Hubs;

public class NotificationHub : Hub
{
    private readonly TotpService _totpService;
    private readonly Data.AppDbContext _db;

    public NotificationHub(TotpService totpService, Data.AppDbContext db)
    {
        _totpService = totpService;
        _db = db;
    }

    /// <summary>
    /// Client calls this after connecting to join a notification group.
    /// Validates TOTP before adding to group.
    /// </summary>
    public async Task JoinGroup(string entityType, int entityId, string totpCode)
    {
        string? secret = null;

        if (entityType == "manager")
        {
            var team = await _db.Teams.FindAsync(entityId);
            secret = team?.ManagerTotpSecret;
        }
        else if (entityType == "reportee")
        {
            var reportee = await _db.Reportees.FindAsync(entityId);
            secret = reportee?.TotpSecret;
        }

        if (secret == null || !_totpService.ValidateTotp(secret, totpCode))
        {
            await Clients.Caller.SendAsync("AuthError", "Invalid TOTP code");
            return;
        }

        var groupName = $"{entityType}_{entityId}";
        await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
        await Clients.Caller.SendAsync("JoinedGroup", groupName);
    }

    public async Task LeaveGroup(string entityType, int entityId)
    {
        var groupName = $"{entityType}_{entityId}";
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, groupName);
    }
}

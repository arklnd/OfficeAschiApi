using System.Text.Json;
using Lib.Net.Http.WebPush;
using Lib.Net.Http.WebPush.Authentication;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OfficeAschiApi.Data;
using OfficeAschiApi.DTOs;
using OfficeAschiApi.Hubs;

namespace OfficeAschiApi.Services;

public class NotificationService
{
    private readonly AppDbContext _db;
    private readonly IHubContext<NotificationHub> _hubContext;
    private readonly PushServiceClient _pushClient;
    private readonly ILogger<NotificationService> _logger;

    public NotificationService(
        AppDbContext db,
        IHubContext<NotificationHub> hubContext,
        IOptions<VapidSettings> vapidOptions,
        ILogger<NotificationService> logger)
    {
        _db = db;
        _hubContext = hubContext;
        _logger = logger;

        var vapid = vapidOptions.Value;
        _pushClient = new PushServiceClient();

        if (!string.IsNullOrEmpty(vapid.PublicKey) && !string.IsNullOrEmpty(vapid.PrivateKey))
        {
            _pushClient.DefaultAuthentication = new VapidAuthentication(
                vapid.PublicKey, vapid.PrivateKey)
            {
                Subject = vapid.Subject
            };
        }
    }

    public async Task SendToReporteeAsync(int reporteeId, NotificationPayload payload)
    {
        var groupName = $"reportee_{reporteeId}";
        await _hubContext.Clients.Group(groupName).SendAsync("ReceiveNotification", payload);
        await SendWebPushToEntityAsync("reportee", reporteeId, payload);
    }

    public async Task SendToManagerAsync(int teamId, NotificationPayload payload)
    {
        var groupName = $"manager_{teamId}";
        await _hubContext.Clients.Group(groupName).SendAsync("ReceiveNotification", payload);
        await SendWebPushToEntityAsync("manager", teamId, payload);
    }

    private async Task SendWebPushToEntityAsync(string entityType, int entityId, NotificationPayload payload)
    {
        var subscriptions = await _db.PushSubscriptions
            .Where(s => s.EntityType == entityType && s.EntityId == entityId)
            .ToListAsync();

        foreach (var sub in subscriptions)
        {
            try
            {
                var pushSub = new Lib.Net.Http.WebPush.PushSubscription
                {
                    Endpoint = sub.Endpoint,
                    Keys = new Dictionary<string, string>
                    {
                        ["p256dh"] = sub.P256dhKey,
                        ["auth"] = sub.AuthKey,
                    },
                };

                var json = JsonSerializer.Serialize(new
                {
                    notification = new
                    {
                        title = payload.Title,
                        body = payload.Body,
                        data = new { url = payload.Url, eventType = payload.EventType, notificationId = payload.NotificationId },
                        icon = "/icons/icon-192x192.png",
                        badge = "/icons/icon-72x72.png",
                    }
                });

                var message = new PushMessage(json) { Topic = payload.EventType, Urgency = PushMessageUrgency.High };
                await _pushClient.RequestPushMessageDeliveryAsync(pushSub, message);
            }
            catch (PushServiceClientException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Gone)
            {
                _logger.LogInformation("Removing stale push subscription {Id} (410 Gone)", sub.Id);
                _db.PushSubscriptions.Remove(sub);
                await _db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send Web Push to subscription {Id}", sub.Id);
            }
        }
    }

    public async Task CleanupSubscriptionsAsync(string entityType, int entityId)
    {
        var subs = await _db.PushSubscriptions
            .Where(s => s.EntityType == entityType && s.EntityId == entityId)
            .ToListAsync();

        if (subs.Count > 0)
        {
            _db.PushSubscriptions.RemoveRange(subs);
            await _db.SaveChangesAsync();
        }
    }
}

namespace OfficeAschiApi.Models;

public class PushSubscription
{
    public int Id { get; set; }
    public int TeamId { get; set; }
    public string EntityType { get; set; } = string.Empty; // "manager" or "reportee"
    public int EntityId { get; set; }
    public string Endpoint { get; set; } = string.Empty;
    public string P256dhKey { get; set; } = string.Empty;
    public string AuthKey { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Team Team { get; set; } = null!;
}

namespace OfficeAschiApi.Models;

public class Reportee
{
    public int Id { get; set; }
    public int TeamId { get; set; }
    public string FriendlyName { get; set; } = string.Empty;
    public bool IsApproved { get; set; }
    public string? TotpSecret { get; set; } // set after TOTP setup

    public Team Team { get; set; } = null!;
    public List<Booking> Bookings { get; set; } = [];
}

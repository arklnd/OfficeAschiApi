namespace OfficeAschiApi.Models;

public class Seat
{
    public int Id { get; set; }
    public int TeamId { get; set; }
    public string Label { get; set; } = string.Empty;

    public Team Team { get; set; } = null!;
    public List<Booking> Bookings { get; set; } = [];
}

namespace OfficeAschiApi.Models;

public enum BookingStatus
{
    Confirmed,
    Waitlisted
}

public class Booking
{
    public int Id { get; set; }
    public DateOnly Date { get; set; }
    public int SeatId { get; set; }
    public int ReporteeId { get; set; }
    public int TeamId { get; set; }
    public BookingStatus Status { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Seat Seat { get; set; } = null!;
    public Reportee Reportee { get; set; } = null!;
    public Team Team { get; set; } = null!;
}

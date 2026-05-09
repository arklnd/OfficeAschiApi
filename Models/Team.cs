namespace OfficeAschiApi.Models;

public class Team
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty; // friendly name or "team-{id}"
    public string? ManagerTotpSecret { get; set; } // set after TOTP setup

    public List<Seat> Seats { get; set; } = [];
    public List<Reportee> Reportees { get; set; } = [];
}

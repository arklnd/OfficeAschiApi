namespace OfficeAschiApi.DTOs;

// --- Team ---
public record CreateTeamRequest(string? Name);
public record TeamResponse(int Id, string Name, bool HasTotpSetup);

// --- TOTP Setup ---
public record TotpSetupRequest(string SecretKey, string TotpCode);
public record TotpSetupResponse(bool Success, string Message);

// --- Seat ---
public record AddSeatRequest(string Label);
public record SeatResponse(int Id, string Label, int TeamId);

// --- Reportee ---
public record JoinTeamRequest(string FriendlyName);
public record ReporteeResponse(int Id, string FriendlyName, int TeamId, bool IsApproved, bool HasTotpSetup);

// --- Booking ---
public record BookSeatRequest(int ReporteeId, int SeatId, DateOnly Date);
public record CancelBookingRequest(int ReporteeId);
public record BookingResponse(int Id, DateOnly Date, int SeatId, string SeatLabel, int ReporteeId,
    string ReporteeName, string Status, DateTime CreatedAt);

// --- Availability ---
public record AvailabilityResponse(
    DateOnly Date,
    int TotalSeats,
    int BookedCount,
    int AvailableCount,
    int WaitlistedCount,
    List<BookingResponse> Bookings,
    List<SeatResponse> AvailableSeats,
    List<WaitlistInfo> Waitlist);

public record WaitlistInfo(int BookingId, string ReporteeName, string DesiredSeatLabel, DateTime WaitlistedSince);

// --- Search ---
public record TeamSearchResult(int Id, string Name, int SeatCount, int MemberCount);

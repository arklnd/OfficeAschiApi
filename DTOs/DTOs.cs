namespace OfficeAschiApi.DTOs;

// --- Team ---
public record CreateTeamRequest(string? Name, string SecretKey, string TotpCode);
public record TeamResponse(int Id, string Name, bool HasTotpSetup);

// --- Seat ---
public record AddSeatRequest(string Label);
public record SeatResponse(int Id, string Label, int TeamId);

// --- Reportee ---
public record JoinTeamRequest(string FriendlyName, string SecretKey, string TotpCode);
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

// --- Push Notifications ---
public record PushSubscriptionRequest(string Endpoint, string P256dhKey, string AuthKey);
public record NotificationPayload(string Title, string Body, string? Url, string EventType, string? NotificationId = null);

using System.ComponentModel.DataAnnotations;

namespace OfficeAschiApi.DTOs;

// --- Team ---
public record CreateTeamRequest(string? Name, [Required] string SecretKey, [Required] string TotpCode);
public record TeamResponse(int Id, string Name, bool HasTotpSetup);

// --- Seat ---
public record AddSeatRequest([Required] string Label);
public record SeatResponse(int Id, string Label, int TeamId);

// --- Reportee ---
public record JoinTeamRequest([Required] string FriendlyName, [Required] string SecretKey, [Required] string TotpCode);
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

// --- Date-range availability ---
public record DateAvailabilitySummary(
    DateOnly Date,
    int TotalSeats,
    int BookedCount,
    int AvailableCount,
    int WaitlistedCount);

public record RangeAvailabilityResponse(
    int TeamId,
    DateOnly From,
    DateOnly To,
    List<DateAvailabilitySummary> Days);

// --- Date-range booking ---
public record BookSeatRangeRequest(int ReporteeId, int SeatId, DateOnly From, DateOnly To, bool SkipWeekends = true);

public record RangeBookingResult(
    DateOnly Date,
    bool Success,
    string Status,
    int? BookingId,
    string? Error);

public record RangeBookingResponse(
    int SeatId,
    string SeatLabel,
    int ReporteeId,
    string ReporteeName,
    DateOnly From,
    DateOnly To,
    int ConfirmedCount,
    int WaitlistedCount,
    int FailedCount,
    List<RangeBookingResult> Results);

// --- Search ---
public record TeamSearchResult(int Id, string Name, int SeatCount, int MemberCount);

// --- All Seats Overview ---
public record SeatOverviewBooking(
    int ReporteeId,
    string ReporteeName,
    int BookingId,
    string Status,
    DateTime CreatedAt);

public record SeatOverviewResponse(
    int Id,
    string Label,
    int TeamId,
    string TeamName,
    bool IsEngaged,
    SeatOverviewBooking? EngagedBy);

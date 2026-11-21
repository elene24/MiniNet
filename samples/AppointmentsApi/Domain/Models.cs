using System.ComponentModel.DataAnnotations;

namespace AppointmentsApi.Domain;

public sealed record Doctor(int Id, string Name, string Specialty);

public sealed record Appointment(Guid Id, int DoctorId, string PatientName, DateTimeOffset StartsAt, DateTimeOffset CreatedAt);

public sealed class BookAppointmentRequest
{
    [Required, StringLength(100, MinimumLength = 2)]
    public string PatientName { get; set; } = string.Empty;

    /// <summary>Slot start, with an explicit offset, for example "2026-12-01T10:30:00+04:00".</summary>
    [Required]
    public DateTimeOffset? StartsAt { get; set; }
}

public enum BookingStatus
{
    Booked,
    DoctorNotFound,
    InvalidSlot,
    SlotTaken,
}

public sealed record BookingResult(BookingStatus Status, Appointment? Appointment = null, string? Reason = null);

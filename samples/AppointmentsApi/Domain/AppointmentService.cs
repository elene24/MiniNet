using AppointmentsApi.Data;

namespace AppointmentsApi.Domain;

/// <summary>Booking rules. Registered as scoped: one instance per request.</summary>
public sealed class AppointmentService(IAppointmentRepository repository, ClinicSchedule schedule, TimeProvider time)
{
    public async Task<BookingResult> BookAsync(int doctorId, BookAppointmentRequest request, CancellationToken cancellationToken)
    {
        if (await repository.GetDoctorAsync(doctorId, cancellationToken) is null)
            return new BookingResult(BookingStatus.DoctorNotFound);

        var now = time.GetUtcNow();
        var startsAt = request.StartsAt!.Value.ToOffset(schedule.UtcOffset);
        if (schedule.Validate(startsAt, now) is { } reason)
            return new BookingResult(BookingStatus.InvalidSlot, Reason: reason);

        var appointment = new Appointment(Guid.NewGuid(), doctorId, request.PatientName.Trim(), startsAt, now);
        return await repository.TryBookAsync(appointment, cancellationToken)
            ? new BookingResult(BookingStatus.Booked, appointment)
            : new BookingResult(BookingStatus.SlotTaken, Reason: "This slot has just been booked by someone else.");
    }

    /// <summary>Free slots for a doctor on a local date, skipping past and booked slots.</summary>
    public async Task<IReadOnlyList<DateTimeOffset>?> GetFreeSlotsAsync(int doctorId, DateOnly date, CancellationToken cancellationToken)
    {
        if (await repository.GetDoctorAsync(doctorId, cancellationToken) is null) return null;

        var (from, to) = schedule.DayRange(date);
        var booked = (await repository.GetAppointmentsAsync(doctorId, from, to, cancellationToken))
            .Select(a => a.StartsAt.UtcDateTime)
            .ToHashSet();
        var now = time.GetUtcNow();

        return schedule.SlotsOn(date)
            .Where(slot => slot > now && !booked.Contains(slot.UtcDateTime))
            .ToList();
    }
}

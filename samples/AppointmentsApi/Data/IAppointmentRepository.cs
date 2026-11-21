using AppointmentsApi.Domain;

namespace AppointmentsApi.Data;

public interface IAppointmentRepository
{
    Task<IReadOnlyList<Doctor>> GetDoctorsAsync(string? specialty, CancellationToken cancellationToken);

    Task<Doctor?> GetDoctorAsync(int id, CancellationToken cancellationToken);

    Task<IReadOnlyList<Appointment>> GetAppointmentsAsync(int doctorId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);

    Task<Appointment?> GetAppointmentAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Stores the appointment only if the doctor's slot is still free, as one atomic operation.
    /// Returns false if someone else booked the slot first. "Check, then insert" as two separate
    /// steps would let two concurrent requests both see the slot as free.
    /// </summary>
    Task<bool> TryBookAsync(Appointment appointment, CancellationToken cancellationToken);

    Task<bool> CancelAsync(Guid id, CancellationToken cancellationToken);
}

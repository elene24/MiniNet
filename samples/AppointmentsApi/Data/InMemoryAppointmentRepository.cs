using AppointmentsApi.Domain;

namespace AppointmentsApi.Data;

/// <summary>Used when no database is configured: local development and tests. Registered as a singleton.</summary>
public sealed class InMemoryAppointmentRepository : IAppointmentRepository
{
    private readonly object _lock = new();
    private readonly List<Doctor> _doctors = SeedData.Doctors.ToList();
    private readonly Dictionary<Guid, Appointment> _appointments = new();
    private readonly HashSet<(int DoctorId, DateTimeOffset StartsAt)> _takenSlots = new();

    public Task<IReadOnlyList<Doctor>> GetDoctorsAsync(string? specialty, CancellationToken cancellationToken)
    {
        IReadOnlyList<Doctor> result = _doctors
            .Where(d => specialty is null || string.Equals(d.Specialty, specialty, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return Task.FromResult(result);
    }

    public Task<Doctor?> GetDoctorAsync(int id, CancellationToken cancellationToken) =>
        Task.FromResult(_doctors.FirstOrDefault(d => d.Id == id));

    public Task<IReadOnlyList<Appointment>> GetAppointmentsAsync(int doctorId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            IReadOnlyList<Appointment> result = _appointments.Values
                .Where(a => a.DoctorId == doctorId && a.StartsAt >= from && a.StartsAt < to)
                .OrderBy(a => a.StartsAt)
                .ToList();
            return Task.FromResult(result);
        }
    }

    public Task<Appointment?> GetAppointmentAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_lock) return Task.FromResult(_appointments.GetValueOrDefault(id));
    }

    public Task<bool> TryBookAsync(Appointment appointment, CancellationToken cancellationToken)
    {
        // The lock makes "is it free?" and "take it" one step, the in-memory equivalent of the
        // database's unique index on (DoctorId, StartsAt).
        lock (_lock)
        {
            if (!_takenSlots.Add((appointment.DoctorId, appointment.StartsAt.ToUniversalTime()))) return Task.FromResult(false);
            _appointments[appointment.Id] = appointment;
            return Task.FromResult(true);
        }
    }

    public Task<bool> CancelAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (!_appointments.Remove(id, out var appointment)) return Task.FromResult(false);
            _takenSlots.Remove((appointment.DoctorId, appointment.StartsAt.ToUniversalTime()));
            return Task.FromResult(true);
        }
    }
}

public static class SeedData
{
    public static readonly IReadOnlyList<Doctor> Doctors =
    [
        new(1, "Dr. Nino Beridze", "cardiology"),
        new(2, "Dr. Giorgi Kapanadze", "dermatology"),
        new(3, "Dr. Mariam Lomidze", "pediatrics"),
        new(4, "Dr. Levan Tsiklauri", "cardiology"),
    ];
}

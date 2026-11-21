using System.Data;
using System.Data.Common;
using AppointmentsApi.Domain;

namespace AppointmentsApi.Data;

/// <summary>
/// Azure SQL / SQL Server storage, written against the provider-neutral ADO.NET base classes
/// (<see cref="DbConnection"/>, <see cref="DbCommand"/>). The concrete driver (Microsoft.Data.SqlClient)
/// is plugged in at startup through <see cref="DbProviderFactories"/>.
/// All queries are parameterized, so user input is never concatenated into SQL.
/// </summary>
public sealed class SqlAppointmentRepository(Func<DbConnection> connectionFactory) : IAppointmentRepository
{
    public const string ProviderName = "Microsoft.Data.SqlClient";

    /// <summary>Creates a connection factory for SQL Server without a compile-time reference to the driver.</summary>
    public static Func<DbConnection> CreateConnectionFactory(string connectionString)
    {
        DbProviderFactories.RegisterFactory(ProviderName, "Microsoft.Data.SqlClient.SqlClientFactory, Microsoft.Data.SqlClient");
        var factory = DbProviderFactories.GetFactory(ProviderName);
        return () =>
        {
            var connection = factory.CreateConnection()!;
            connection.ConnectionString = connectionString;
            return connection;
        };
    }

    /// <summary>Runs infra/sql/schema.sql, which is idempotent, so the app can start against an empty database.</summary>
    public async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        await using var stream = typeof(SqlAppointmentRepository).Assembly.GetManifestResourceStream("schema.sql")
            ?? throw new InvalidOperationException("Embedded schema.sql not found.");
        using var reader = new StreamReader(stream);
        var script = await reader.ReadToEndAsync(cancellationToken);

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = script;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Doctor>> GetDoctorsAsync(string? specialty, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Name, Specialty FROM dbo.Doctors
            WHERE @Specialty IS NULL OR Specialty = @Specialty
            ORDER BY Id
            """;
        AddParameter(command, "@Specialty", DbType.String, (object?)specialty ?? DBNull.Value);

        var doctors = new List<Doctor>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) doctors.Add(ReadDoctor(reader));
        return doctors;
    }

    public async Task<Doctor?> GetDoctorAsync(int id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Name, Specialty FROM dbo.Doctors WHERE Id = @Id";
        AddParameter(command, "@Id", DbType.Int32, id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadDoctor(reader) : null;
    }

    public async Task<IReadOnlyList<Appointment>> GetAppointmentsAsync(int doctorId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, DoctorId, PatientName, StartsAt, CreatedAt FROM dbo.Appointments
            WHERE DoctorId = @DoctorId AND StartsAt >= @From AND StartsAt < @To
            ORDER BY StartsAt
            """;
        AddParameter(command, "@DoctorId", DbType.Int32, doctorId);
        AddParameter(command, "@From", DbType.DateTimeOffset, from);
        AddParameter(command, "@To", DbType.DateTimeOffset, to);

        var appointments = new List<Appointment>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) appointments.Add(ReadAppointment(reader));
        return appointments;
    }

    public async Task<Appointment?> GetAppointmentAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, DoctorId, PatientName, StartsAt, CreatedAt FROM dbo.Appointments WHERE Id = @Id";
        AddParameter(command, "@Id", DbType.Guid, id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadAppointment(reader) : null;
    }

    public async Task<bool> TryBookAsync(Appointment appointment, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        // Insert only if the slot is free, in one statement. UPDLOCK + HOLDLOCK lock the checked key range
        // until the insert finishes, so two concurrent bookings for the same slot cannot both pass the check.
        // The unique index UX_Appointments_Doctor_Slot is the final safety net.
        command.CommandText = """
            INSERT INTO dbo.Appointments (Id, DoctorId, PatientName, StartsAt, CreatedAt)
            SELECT @Id, @DoctorId, @PatientName, @StartsAt, @CreatedAt
            WHERE NOT EXISTS (
                SELECT 1 FROM dbo.Appointments WITH (UPDLOCK, HOLDLOCK)
                WHERE DoctorId = @DoctorId AND StartsAt = @StartsAt)
            """;
        AddParameter(command, "@Id", DbType.Guid, appointment.Id);
        AddParameter(command, "@DoctorId", DbType.Int32, appointment.DoctorId);
        AddParameter(command, "@PatientName", DbType.String, appointment.PatientName);
        AddParameter(command, "@StartsAt", DbType.DateTimeOffset, appointment.StartsAt);
        AddParameter(command, "@CreatedAt", DbType.DateTimeOffset, appointment.CreatedAt);

        try
        {
            return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        }
        catch (DbException)
        {
            // The unique index rejected a race the lock hints did not prevent: still just "slot taken".
            // Any other database error is rethrown.
            if (await SlotTakenAsync(appointment, cancellationToken)) return false;
            throw;
        }
    }

    public async Task<bool> CancelAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM dbo.Appointments WHERE Id = @Id";
        AddParameter(command, "@Id", DbType.Guid, id);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private async Task<bool> SlotTakenAsync(Appointment appointment, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM dbo.Appointments WHERE DoctorId = @DoctorId AND StartsAt = @StartsAt";
        AddParameter(command, "@DoctorId", DbType.Int32, appointment.DoctorId);
        AddParameter(command, "@StartsAt", DbType.DateTimeOffset, appointment.StartsAt);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) > 0;
    }

    private async Task<DbConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = connectionFactory();
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static void AddParameter(DbCommand command, string name, DbType type, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = type;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static Doctor ReadDoctor(DbDataReader reader) =>
        new(reader.GetInt32(0), reader.GetString(1), reader.GetString(2));

    private static Appointment ReadAppointment(DbDataReader reader) =>
        new(reader.GetGuid(0), reader.GetInt32(1), reader.GetString(2),
            reader.GetFieldValue<DateTimeOffset>(3), reader.GetFieldValue<DateTimeOffset>(4));
}
